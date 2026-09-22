using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Logic;
using YueToLogic.Core.Model;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class LogicProjectWriterTests
{
    private const int TemplateSongSamples = 14_399_936; // audio.flac of the template song, 48 kHz

    private static readonly byte[] TemplateProjectData = LogicTemplate.Default.Files[LogicTemplate.ProjectDataPath];

    /// <summary>The MIDI tracks of the bundled template, in the order its arrangement places them.</summary>
    private static readonly string[] TemplateTracks =
        ["Ins", "Chords", "Bass", "Guide", "Drums", "Kick", "Snare", "HiHat", "Crash", "Vocal", "Vocal 8vb"];

    [Fact]
    public void Embedded_template_round_trips_byte_for_byte()
    {
        Assert.Equal(TemplateProjectData, LogicProjectData.Parse(TemplateProjectData).Serialize());
    }

    [Fact]
    public async Task Notes_are_encoded_exactly_as_logic_stores_them()
    {
        // The template was created in Logic from this very song; regenerating it must reproduce Logic's bytes.
        var score = Convert(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logic-template-song.abc")), withAccompaniment: true);
        var package = await WriteAsync(score, Flac(48000, 2, 24, TemplateSongSamples));

        var original = RegionEvents(TemplateProjectData);
        var generated = RegionEvents(package.ProjectData);

        // Logic placed each region where its notes begin, this writer at bar 1, so positions count from the
        // first note. Everything else - pitch, velocity, length - has to be byte for byte what Logic wrote.
        foreach (var region in new[] { "Vocal", "Ins", "Bass", "Guide", "Vocal 8vb", "Kick", "Snare", "HiHat", "Crash" })
        {
            var (expected, actual) = (NotesFromFirst(original[region]), NotesFromFirst(generated[region]));
            Assert.True(expected.SequenceEqual(actual), $"{region} differs from Logic's encoding");
        }

        // The template also carries the drum kit on one track, which is a conversion of its own.
        var kit = await WriteAsync(
            Convert(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logic-template-song.abc")), withAccompaniment: true, splitDrums: false),
            Flac(48000, 2, 24, TemplateSongSamples));
        Assert.Equal(NotesFromFirst(original["Drums"]), NotesFromFirst(RegionEvents(kit.ProjectData)["Drums"]));

        // Logic additionally stores the chord names as text events; the notes themselves must match.
        Assert.Equal(NotesFromFirst(original["Chords"]), NotesFromFirst(generated["Chords"]));
    }

    [Fact]
    public async Task Tempo_meter_lengths_and_audio_are_set_from_the_score()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: false); // 88 BPM, 4/4, 8 bars
        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273));

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        const uint songTicks = 8 * 3840;

        // The template's tempo is gone from the project, and every tempo set now runs at the score's tempo.
        Assert.All(
            chunks.Where(c => c.Tag == "EvSq" && c.Class == 3),
            tempoList => Assert.Equal(880_000u, ReadUInt32(tempoList.Payload, 16)));
        Assert.Equal(0, CountUInt32(package.ProjectData, 1_380_000));

        var sequences = chunks.Where(c => c.Tag == "MSeq" && c.Class == 23).ToList();
        foreach (var sequence in sequences.Where(s => s.Id == 4 || TemplateTracks.Contains(s.SequenceName)))
        {
            Assert.Equal(songTicks, ReadUInt32(sequence.Payload, sequence.SequenceLengthOffset));
        }

        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        var placements = Enumerable.Range(0, arrangement.Length / 16 - 1)
            .Select(i => i * 16)
            .Where(i => arrangement[i] is 0x20 or 0x24 && arrangement[i + 23] == 0x89)
            .Select(i => ReadUInt32(arrangement, i + 4))
            .ToList();
        Assert.Equal(TemplateTracks.Length + 1, placements.Count); // the MIDI tracks and the audio track
        Assert.All(placements, p => Assert.Equal(34_560u, p));

        // Length and format sit behind the format tag, whose position depends on what the object holds in
        // front of it; writing them at a fixed offset would leave the template's own length in the project.
        var audioFile = chunks.Single(c => c.Tag == "AuFl").Payload;
        var format = audioFile.AsSpan().LastIndexOf("CaLf"u8);
        Assert.Equal(1_047_273UL, BinaryPrimitives.ReadUInt64LittleEndian(audioFile.AsSpan(format + 12)));
        Assert.Equal(48_000u, ReadUInt32(audioFile, format + 20));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(audioFile.AsSpan(format + 24)));
        Assert.Equal(0, CountUInt64(audioFile, TemplateSongSamples)); // no trace of the template's own audio
        Assert.Equal(-1, audioFile.AsSpan().IndexOf("/Volumes/"u8));
        Assert.Equal(1_047_273UL, BinaryPrimitives.ReadUInt64LittleEndian(chunks.Single(c => c.Tag == "AuRg").Payload.AsSpan(22)));

        var vocal = RegionEvents(package.ProjectData)["Vocal"];
        Assert.Equal(56, NoteRecords(vocal).Count);
        Assert.Empty(NoteRecords(RegionEvents(package.ProjectData)["Bass"]));
        Assert.Equal(38_400u, ReadUInt32(vocal, 4)); // first note on bar 1, beat 1

        Assert.Equal("88", PlistValue(package.Files[LogicTemplate.MetaDataPath], "BeatsPerMinute"));
        Assert.Equal("4", PlistValue(package.Files[LogicTemplate.MetaDataPath], "SongSignatureNumerator"));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(package.ProjectData.AsSpan(16)), (uint)(package.ProjectData.Length - 24));
    }

    [Fact]
    public async Task Meter_other_than_four_four_is_written_to_the_signature_list()
    {
        var score = Convert(Native("V: Vocal\nC8D8E8|F8G8A8|", meter: "3/4"), withAccompaniment: false);

        var package = await WriteAsync(score, Flac(48000, 2, 24, 48_000));

        var signature = LogicProjectData.Parse(package.ProjectData).Chunks.Single(c => c.Tag == "EvSq" && c.Class == 1 && c.Payload[0] == 0x30).Payload;
        Assert.Equal((2, 3), (signature[11], signature[12]));
        Assert.Equal("3", PlistValue(package.Files[LogicTemplate.MetaDataPath], "SongSignatureNumerator"));
    }

    [Fact]
    public async Task Key_and_meter_changes_are_written_exactly_as_logic_stores_them()
    {
        // The template project, with E major at bar 5, F minor at bar 9, 3/4 at bar 13 and 6/8 at bar 17
        // entered on Logic's signature track; the bytes below are the signature list Logic then saved.
        const string logicSignatureList =
            "30000000000000000000000204000000" + // 4/4 at the start of the song
            "3000000000000088F6FF000000960000" +
            "00000000000000880000000007000000" +
            "32000000000000000000000007000000" + // C major at the start of the song
            "00000000000000880000000000000000" +
            "3200000000D20000000000000B000000" + // E major, bar 5
            "00000000000000880000000000000000" +
            "32000000000E01000000000013000000" + // F minor, bar 9
            "00000000000000880000000000000000" +
            "30000000004A01000000000203000000" + // 3/4, bar 13
            "30000000000000880C000000004A0100" +
            "00000000000000880000000000000000" +
            "30000000007701000000000306000080" + // 6/8, bar 17
            "30000000000000881000000000770100" +
            "00000000000000880000000000000000" +
            "F1000000FFFFFF3F0000000000000000";

        var bars = string.Join("\n", Enumerable.Repeat("C16|", 4));
        var score = Convert(Native($"""
            V: Vocal
            {bars}
            V: Ins
            Z4|
            V: Vocal
            K:E
            {bars}
            V: Ins
            K:E
            Z4|
            V: Vocal
            K:Fm
            {bars}
            V: Ins
            K:Fm
            Z4|
            V: Vocal
            M:3/4
            C12|C12|C12|C12|
            V: Ins
            M:3/4
            Z4|
            V: Vocal
            M:6/8
            C12|C12|C12|C12|
            V: Ins
            M:6/8
            Z4|
            """), withAccompaniment: false);
        Assert.Equal([0L, 4 * Bar, 8 * Bar], score.KeySignatures.Select(k => k.StartTicks));

        var package = await WriteAsync(score, Flac(48000, 2, 24, 48_000));

        var signatures = LogicProjectData.Parse(package.ProjectData).Chunks.Single(c => c.Tag == "EvSq" && c.Class == 1 && c.Payload[0] == 0x30).Payload;
        Assert.Equal(logicSignatureList, System.Convert.ToHexString(signatures));
    }

    [Fact]
    public async Task Chord_regions_carry_the_key_they_sound_in()
    {
        // Logic notes the key at each chord in its chord region and derives the suggested chord scale from it.
        var score = Convert(Native("""
            V: Vocal
            "C"C16|"G"C16|
            V: Ins
            Z2|
            V: Vocal
            K:Fm
            "Fm"C16|"Bbm"C16|
            V: Ins
            K:Fm
            Z2|
            """), withAccompaniment: false);

        var package = await WriteAsync(score, Flac(48000, 2, 24, 48_000));

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        var track = chunks.Single(c => c.Tag == "MSeq" && c.Class == 23 && c.SequenceName == "Global Harmonies");
        var placements = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == track.Id).Payload;
        var keys = Enumerable.Range(0, placements.Length / 80)
            .Select(i => ReadUInt32(placements, (i * 80) + 32))
            .Select(id => chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == id).Payload)
            .Select(events => (events[12], events[15]))
            .ToList();

        // C major (7) without the flag, then F minor (3 flats plus 16) as the key of a key change.
        Assert.Equal([((byte)7, (byte)0), (7, 0), (19, 0x80), (19, 0x80)], keys);
    }

    [Fact]
    public async Task Chord_pattern_reaches_the_logic_chord_region()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.Offbeat } },
        };
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var package = await WriteAsync(result.Score!, Flac(48000, 2, 24, 1_047_273));

        // Four off-beat chords per bar, three notes each, exactly as the score's own chord track has them.
        var region = RegionEvents(package.ProjectData)["Chords"];
        Assert.Equal(8 * 4 * 3, NoteRecords(region).Count);
        Assert.Equal(result.Score!.Voice("Chords").Notes.Count, NoteRecords(region).Count);
        Assert.Equal(38_400u + Ppq, ReadUInt32(region, 4)); // first chord on the second eighth (Logic counts double)
    }

    [Fact]
    public async Task The_audio_is_named_as_the_package_writes_it()
    {
        // The template's audio is called "audio_1.flac", because Logic numbers a file imported into a project
        // that already had one. The package always writes "audio.flac", and Logic looks for the name in the
        // project - in the file object as UTF-16 and in its region as UTF-8.
        var templateName = "audio_1.flac";
        Assert.Contains(System.Text.Encoding.Unicode.GetBytes(templateName), TemplateProjectData);

        var package = await WriteAsync(Convert(File.ReadAllText(SamplePath), false), Flac(48000, 2, 24, 1_047_273));

        foreach (var encoding in new[] { System.Text.Encoding.Unicode, System.Text.Encoding.UTF8 })
        {
            Assert.DoesNotContain(encoding.GetBytes(templateName), package.ProjectData);
            Assert.Contains(encoding.GetBytes("audio.flac"), package.ProjectData);
        }

        Assert.Equal("Audio Files/audio.flac", PlistValue(package.Files[LogicTemplate.MetaDataPath], "AudioFiles"));
    }

    [Fact]
    public async Task A_track_plays_on_the_channel_it_was_given()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions { Channels = new Dictionary<string, int> { ["bass"] = 7 } };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        // Logic stores the channel in every note record; the other tracks keep the template's.
        var regions = RegionEvents(package.ProjectData);
        Assert.All(NoteRecords(regions["Bass"]), record => Assert.Equal("96", record[..2])); // 0x90 | 6
        Assert.All(NoteRecords(regions["Drums"]), record => Assert.Equal("99", record[..2])); // drums on 10
    }

    [Fact]
    public async Task A_track_with_an_instrument_is_named_after_both()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument>
            {
                ["bass"] = new() { Name = " Mother32 ", Port = "MIDI4x4 Midi Out 1", Channel = 12 },
            },
        };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        // The track header shows which synthesizer plays the part; the other tracks read as before.
        var names = TrackNames(LogicProjectData.Parse(package.ProjectData).Chunks);
        Assert.Contains("Bass · Mother32", names);
        Assert.Contains("Vocal", names);
        Assert.DoesNotContain("Bass", names);
    }

    [Fact]
    public async Task An_instrument_puts_its_channel_on_the_track_ahead_of_the_channel_option()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Channels = new Dictionary<string, int> { ["Bass"] = 7 },
            Instruments = new Dictionary<string, LogicInstrument>
            {
                ["Bass"] = new() { Name = "Mother32", Port = "MIDI4x4 Midi Out 1", Channel = 12 },
            },
        };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        var regions = RegionEvents(package.ProjectData);
        Assert.All(NoteRecords(regions["Bass"]), record => Assert.Equal("9B", record[..2])); // 0x90 | 11
        Assert.All(NoteRecords(regions["Drums"]), record => Assert.Equal("99", record[..2])); // drums on 10
    }

    [Fact]
    public async Task An_instrument_without_a_usable_channel_keeps_the_template_channel_but_still_names_the_track()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var plain = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273));
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument> { ["Bass"] = new() { Name = "Mother32", Channel = 0 } },
        };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        Assert.Equal(NoteRecords(RegionEvents(plain.ProjectData)["Bass"]), NoteRecords(RegionEvents(package.ProjectData)["Bass"]));
        Assert.Contains("Bass · Mother32", TrackNames(LogicProjectData.Parse(package.ProjectData).Chunks));
    }

    [Fact]
    public async Task Instrument_names_survive_splitting_at_sections()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            SplitRegionsAtSections = true,
            Instruments = new Dictionary<string, LogicInstrument> { ["Bass"] = new() { Name = "Mother32", Channel = 12 } },
        };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        Assert.Contains("Bass · Mother32", TrackNames(LogicProjectData.Parse(package.ProjectData).Chunks));
    }

    [Fact]
    public async Task An_instrument_with_a_known_output_puts_an_external_instrument_on_the_track()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument>
            {
                ["Bass"] = new() { Name = "Mother32", Port = "MIDI4x4 Midi Out 1", Channel = 12 },
            },
        };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        // The template's Alchemy in the bass strip's instrument slot gives way to Logic's External Instrument.
        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        Assert.Equal("Alchemy", PluginName(InstrumentSlot(LogicProjectData.Parse(TemplateProjectData).Chunks, "Bass")));
        var slot = InstrumentSlot(chunks, "Bass");
        Assert.Equal("External", PluginName(slot));
        Assert.Equal(568, slot.Payload.Length);

        // The destination is written three times, as Logic saves it: display name, position in the output list, CoreMIDI entry.
        Assert.Equal("MIDI4x4 Midi Out 1", Text(slot.Payload, 196, 128));
        Assert.Equal(2u, ReadUInt32(slot.Payload, 336)); // second output of the template's list, counted from one
        Assert.Equal(12u, ReadUInt32(slot.Payload, 344));
        Assert.Equal(917_622_827, BinaryPrimitives.ReadInt32LittleEndian(slot.Payload.AsSpan(448))); // the unique id the template's Mac gave the port
        Assert.Equal("Midi Out 1", Text(slot.Payload, 452, 64));
        Assert.Equal("MIDI4x4", Text(slot.Payload, 516, 32));

        // Switched on, not bypassed: Logic keeps that in the state's header and in the first parameter.
        Assert.Equal(0, slot.Payload[112]);
        Assert.Equal(0u, ReadUInt32(slot.Payload, 328));

        // The track's MIDI input is off, so a keyboard does not play the hardware through every routed track.
        Assert.Equal(0x3f, Strip(chunks, "Bass").Payload[32]);
        Assert.Equal(0x3e, Strip(chunks, "Vocal").Payload[32]);

        // The strip object says the instrument is external (else Logic shows it switched off), and the channel
        // strip setting the template's sound came from ("Agile Synth Bass") is gone with the sound.
        Assert.Equal(1, StripObject(chunks, "Bass").Payload[110]);
        Assert.Equal(0, StripObject(chunks, "Vocal").Payload[110]);
        Assert.Equal("", Text(SettingObject(chunks, "Bass").Payload, 16, 64));
        Assert.Equal("Agile Synth Bass", Text(SettingObject(LogicProjectData.Parse(TemplateProjectData).Chunks, "Bass").Payload, 16, 64));
        Assert.Equal("Studio Grand", Text(SettingObject(chunks, "Vocal").Payload, 16, 64));

        // The other tracks keep their software instruments.
        Assert.Equal("Piano", PluginName(InstrumentSlot(chunks, "Vocal")));
    }

    [Fact]
    public async Task An_output_named_like_its_device_is_matched_by_that_one_name()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument>
            {
                ["Vocal"] = new() { Name = "WASP Deluxe", Port = " scarlett 8i6 usb ", Channel = 1 },
            },
        };

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273), options);

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        var slot = InstrumentSlot(chunks, "Vocal");
        Assert.Equal("External", PluginName(slot));
        Assert.Equal("Scarlett 8i6 USB", Text(slot.Payload, 196, 128));
        Assert.Equal(1u, ReadUInt32(slot.Payload, 336));
        Assert.Equal(1u, ReadUInt32(slot.Payload, 344));
        Assert.Equal("Scarlett 8i6 USB", Text(slot.Payload, 452, 64));

        // The template's vocal sound came with an EQ, a compressor and a reverb; a hardware synthesizer brings its own.
        var templateChunks = LogicProjectData.Parse(TemplateProjectData).Chunks;
        Assert.Equal(["Piano", "Channel EQ", "Compressor", "ChromaVerb"], PluginInstances(templateChunks, "Vocal").Select(PluginName));
        Assert.Equal(["External"], PluginInstances(chunks, "Vocal").Select(PluginName));
        Assert.Equal([1, 1, 1, 1], Enumerable.Range(0, 4).Select(i => (int)StripObject(templateChunks, "Vocal").Payload[144 + (4 * i)]));
        Assert.Equal([1, 0, 0, 0], Enumerable.Range(0, 4).Select(i => (int)StripObject(chunks, "Vocal").Payload[144 + (4 * i)]));
        // The smart-control archives and the setting object stay, as Logic leaves them when the sound goes.
        Assert.Equal(StripObjects(templateChunks, "AuCU", "Vocal").Count() - 3, StripObjects(chunks, "AuCU", "Vocal").Count());
    }

    [Fact]
    public async Task An_output_the_template_does_not_know_leaves_the_instrument_and_says_so()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument>
            {
                ["Bass"] = new() { Name = "Mother32", Port = "Fake Port 9", Channel = 12 },
            },
        };

        var sink = new MemorySink();
        var result = await new LogicProjectWriter().WriteAsync(score, new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 1_047_273))), sink, options);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.MidiPortUnknown);
        Assert.Contains("Fake Port 9", warning.Message, StringComparison.Ordinal);
        Assert.Contains("MIDI4x4 Midi Out 1", warning.Message, StringComparison.Ordinal);
        var chunks = LogicProjectData.Parse(sink.Files[LogicTemplate.ProjectDataPath].ToArray()).Chunks;
        Assert.Equal("Alchemy", PluginName(InstrumentSlot(chunks, "Bass")));
        // The name and the channel are still worth having.
        Assert.Contains("Bass · Mother32", TrackNames(chunks));
    }

    [Fact]
    public async Task A_drum_machine_designer_track_cannot_be_routed_and_says_why()
    {
        // The template's Kick, Snare and HiHat are Drum Machine Designer tracks: an aux each, with the sound on strips of its own.
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument> { ["Kick"] = new() { Name = "Drumbrute Impact", Port = "MIDI4x4 Midi Out 2", Channel = 8 } },
        };

        var sink = new MemorySink();
        var result = await new LogicProjectWriter().WriteAsync(score, new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 1_047_273))), sink, options);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.LogicTemplateLimitation && d.Message.Contains("Kick", StringComparison.Ordinal));
        Assert.Contains("Drum Machine Designer", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Drumbrute Impact", warning.Message, StringComparison.Ordinal);
        // The name and the channel still go in.
        Assert.Contains("Kick · Drumbrute Impact", TrackNames(LogicProjectData.Parse(sink.Files[LogicTemplate.ProjectDataPath].ToArray()).Chunks));
    }

    [Fact]
    public async Task An_instrument_without_an_output_keeps_the_template_instrument()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);
        var options = new LogicProjectOptions
        {
            Instruments = new Dictionary<string, LogicInstrument> { ["Bass"] = new() { Name = "Mother32", Channel = 12 } },
        };

        var sink = new MemorySink();
        var result = await new LogicProjectWriter().WriteAsync(score, new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 1_047_273))), sink, options);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.MidiPortUnknown);
        Assert.Equal("Alchemy", PluginName(InstrumentSlot(LogicProjectData.Parse(sink.Files[LogicTemplate.ProjectDataPath].ToArray()).Chunks, "Bass")));
    }

    /// <summary>
    /// The plug-in in the instrument slot of the strip a track lies on: the region names its strip, the strip
    /// carries its number behind its name, and the strip's plug-ins carry that number in their headers.
    /// </summary>
    private static LogicChunk InstrumentSlot(List<LogicChunk> chunks, string track) =>
        PluginInstances(chunks, track).Single(c => BinaryPrimitives.ReadUInt16LittleEndian(c.Payload.AsSpan(6, 2)) == 0);

    /// <summary>The instrument and the inserts of a track's strip, in slot order: the chunks whose payload byte 4 marks a plug-in instance.</summary>
    private static IEnumerable<LogicChunk> PluginInstances(List<LogicChunk> chunks, string track) =>
        StripObjects(chunks, "AuCU", track).Where(c => c.Payload[4] == 1).OrderBy(c => ReadUInt32(c.Header, 18));

    private static LogicChunk StripObject(List<LogicChunk> chunks, string track) => StripObjects(chunks, "AuCO", track).Single();

    /// <summary>The environment object (channel strip) a track's region lies on.</summary>
    private static LogicChunk Strip(List<LogicChunk> chunks, string track)
    {
        var region = chunks.Single(c => c.Tag == "MSeq" && c.Class == 23 && c.SequenceName == track);
        return chunks.Single(c => c.Tag == "Envi" && c.Class == 20 && c.Id == ReadUInt32(region.Payload, region.SequenceLengthOffset - 60 + 204));
    }

    private static LogicChunk SettingObject(List<LogicChunk> chunks, string track) => StripObjects(chunks, "AuCU", track).Single(c => c.Payload.Length == 192);

    private static IEnumerable<LogicChunk> StripObjects(List<LogicChunk> chunks, string tag, string track)
    {
        var region = chunks.Single(c => c.Tag == "MSeq" && c.Class == 23 && c.SequenceName == track);
        var strip = chunks.Single(c => c.Tag == "Envi" && c.Class == 20 && c.Id == ReadUInt32(region.Payload, region.SequenceLengthOffset - 60 + 204));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(strip.Payload.AsSpan(158, 2));
        var number = BinaryPrimitives.ReadUInt16LittleEndian(strip.Payload.AsSpan(160 + nameLength + (nameLength & 1), 2)) - 1u;
        return chunks.Where(c => c.Tag == tag && c.Class == 14 && ReadUInt32(c.Header, 10) == 36 && ReadUInt32(c.Header, 14) == number);
    }

    private static string PluginName(LogicChunk plugin) => Text(plugin.Payload, 120, 12);

    private static string Text(byte[] buffer, int offset, int length)
    {
        var field = buffer.AsSpan(offset, length);
        var end = field.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    [Fact]
    public void An_instrument_read_from_json_may_leave_the_port_out()
    {
        var instruments = System.Text.Json.JsonSerializer.Deserialize(
            """{"Bass":{"name":"Mother32","channel":12}}""",
            Serialization.YueToLogicJsonContext.Default.DictionaryStringLogicInstrument)!;

        var bass = instruments["Bass"];
        Assert.Equal("Mother32", bass.Name);
        Assert.Equal(12, bass.Channel);
        Assert.Equal(string.Empty, bass.Port);
    }

    [Fact]
    public async Task The_stems_go_on_the_audio_tracks_of_their_own()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: false);
        var audio = new LogicAudio(
            new MemoryStream(Flac(48000, 2, 24, 1_047_273)),
            new MemoryStream(Wave(48000, 2, 16, 1_047_000)),
            new MemoryStream(Wave(48000, 2, 16, 1_047_000)));

        var package = await WriteAsync(score, audio);

        // Three files, each on the track that plays it, and the names of the package rather than the template's.
        Assert.Equal(
            ["Media/Audio Files/audio.flac", "Media/Audio Files/vocals.wav", "Media/Audio Files/vocals_dry.wav"],
            package.Files.Keys.Where(k => k.StartsWith("Media/", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        var files = chunks.Where(c => c.Tag == "AuFl").ToList();
        Assert.Equal(3, files.Count);
        Assert.Equal([1_047_273L, 1_047_000, 1_047_000], files.Select(SampleCount));
        Assert.Equal(3, chunks.Count(c => c.Tag == "AuRg"));

        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        Assert.Equal(3, Enumerable.Range(0, arrangement.Length / 80).Count(i => arrangement[i * 80] == 0x24));

        var names = TrackNames(chunks);
        Assert.Equal(["Mix", "Vocals", "Vocals dry"], names.Intersect(["Mix", "Vocals", "Vocals dry"]));
        Assert.Equal(
            ["Audio Files/audio.flac", "Audio Files/vocals.wav", "Audio Files/vocals_dry.wav"],
            PlistValues(package.Files[LogicTemplate.MetaDataPath], "AudioFiles"));
    }

    [Fact]
    public async Task A_stem_that_is_left_out_takes_its_track_with_it()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: false);
        var audio = new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 1_047_273)), Vocals: null, VocalsDry: new MemoryStream(Wave(48000, 2, 16, 1_047_000)));

        var package = await WriteAsync(score, audio);

        // The vocals were left out, so only the mix and the dry vocals are in the package and in the project.
        Assert.DoesNotContain("Media/Audio Files/vocals.wav", package.Files.Keys);
        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        Assert.Equal(2, chunks.Count(c => c.Tag == "AuFl"));
        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        Assert.Equal(2, Enumerable.Range(0, arrangement.Length / 80).Count(i => arrangement[i * 80] == 0x24));
    }

    [Fact]
    public async Task The_project_carries_no_path_of_the_machine_the_template_was_built_on()
    {
        // The template's instruments remember where their samples and impulse responses were found, which is
        // the home folder of whoever saved it. Logic finds its own library content without being told.
        Assert.Contains("/Users/"u8.ToArray(), TemplateProjectData);

        var package = await WriteAsync(Convert(File.ReadAllText(SamplePath), false), Flac(48000, 2, 24, 1_047_273));

        Assert.DoesNotContain("/Users/"u8.ToArray(), package.ProjectData);
        // Paths outside a home folder say nothing about the machine and are left as they are.
        Assert.Equal(
            CountOccurrences(TemplateProjectData, "/Library/"u8.ToArray()),
            CountOccurrences(package.ProjectData, "/Library/"u8.ToArray()));
    }

    [Fact]
    public async Task Clearing_those_paths_leaves_the_instruments_themselves_untouched()
    {
        // The paths sit among the plug-ins' own data. Clearing more than the text of a path destroys a patch,
        // which is why every byte that differs has to have been readable text in the template.
        var package = await WriteAsync(Convert(File.ReadAllText(SamplePath), false), Flac(48000, 2, 24, 1_047_273));

        var template = LogicProjectData.Parse(TemplateProjectData).Chunks.Where(c => c.Tag == "AuCU").ToList();
        var written = LogicProjectData.Parse(package.ProjectData).Chunks.Where(c => c.Tag == "AuCU").ToList();
        Assert.Equal(template.Count, written.Count);

        for (var chunk = 0; chunk < template.Count; chunk++)
        {
            var (before, after) = (template[chunk].Payload, written[chunk].Payload);
            Assert.Equal(before.Length, after.Length);
            for (var i = 0; i < before.Length; i++)
            {
                if (before[i] != after[i])
                {
                    Assert.Equal(0, after[i]);
                    Assert.InRange(before[i], (byte)0x20, (byte)0x7E);
                }
            }
        }
    }

    [Fact]
    public async Task Without_audio_the_project_keeps_an_empty_audio_track()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: false);
        var sink = new MemorySink();

        var result = await new LogicProjectWriter().WriteAsync(score, new LogicAudio(null), sink);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Null(result.Audio);
        Assert.DoesNotContain(LogicTemplate.AudioPath, sink.Files.Keys);
        Assert.Contains(LogicTemplate.ProjectDataPath, sink.Files.Keys);

        var projectData = sink.Files[LogicTemplate.ProjectDataPath].ToArray();
        var chunks = LogicProjectData.Parse(projectData).Chunks;
        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        var heads = Enumerable.Range(0, arrangement.Length / 80).Select(i => arrangement[i * 80]).ToList();
        Assert.Equal(Enumerable.Repeat((byte)0x20, TemplateTracks.Length), heads); // the MIDI regions, without the audio one

        // Logic would report the template's audio file as missing, so file and region are gone, registry included.
        Assert.DoesNotContain(chunks, c => c.Tag is "AuFl" or "AuRg");
        var song = chunks.Single(c => c.Tag == "Song").Payload;
        var templateSong = LogicProjectData.Parse(TemplateProjectData).Chunks.Single(c => c.Tag == "Song").Payload;
        Assert.Equal(2, CountEntries(templateSong, 11, 0)); // one entry in each of the two registry tables
        Assert.Equal(0, CountEntries(song, 11, 0));
        Assert.Equal(0, CountEntries(song, 11, 4)); // the template's second audio file goes as well

        var metaData = System.Text.Encoding.UTF8.GetString(sink.Files[LogicTemplate.MetaDataPath].ToArray());
        Assert.DoesNotContain("audio.flac", metaData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audio_is_copied_unchanged_into_the_package()
    {
        var flac = Flac(48000, 2, 24, 1_047_273);

        var package = await WriteAsync(Convert(File.ReadAllText(SamplePath), false), flac);

        Assert.Equal(flac, package.Files[LogicTemplate.AudioPath]);
        Assert.Contains("Alternatives/000/DisplayState.plist", package.Files.Keys);
    }

    [Theory]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 0x04 }, DiagnosticCodes.InvalidAudio)]
    [InlineData(null, DiagnosticCodes.UnsupportedSampleRate)]
    public async Task Unusable_audio_is_rejected_before_anything_is_written(byte[]? notFlac, string code)
    {
        var sink = new MemorySink();
        var audio = notFlac ?? Flac(44100, 2, 16, 44_100);

        var result = await new LogicProjectWriter().WriteAsync(Convert(File.ReadAllText(SamplePath), false), new LogicAudio(new MemoryStream(audio)), sink);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(sink.Files);
    }

    [Fact]
    public async Task Audio_of_a_different_length_is_reported()
    {
        var result = await new LogicProjectWriter().WriteAsync(Convert(File.ReadAllText(SamplePath), false), new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 48_000 * 120))), new MemorySink());

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.AudioLengthMismatch);
    }

    [Fact]
    public void Chords_are_encoded_exactly_as_logic_stores_them()
    {
        // The template's chord track holds these chords, entered by hand in Logic, one per bar.
        string[] texts = ["C", "Cm", "Cdim", "Caug", "C7", "Cmaj7", "Cm7", "Cdim7", "Cm7b5", "Csus4", "Csus2", "C6", "Cm6", "C7sus4", "Cm(maj7)", "C/E", "F#m7"];
        var logicRecords = TemplateChordRecords();

        for (var i = 0; i < texts.Length; i++)
        {
            Assert.True(ChordSymbolParser.TryParse(texts[i], out var symbol));
            var record = logicRecords[i].ToArray();
            LogicChordEncoding.Encode(record, texts[i], symbol);

            // Logic picked a phrygian scale for F#m7 (context-dependent); we always use the quality's default scale.
            var compared = texts[i] == "F#m7" ? 14 : 16;
            Assert.True(logicRecords[i][..compared].SequenceEqual(record[..compared]), $"{texts[i]} differs from Logic's encoding");
        }
    }

    [Fact]
    public void Flats_and_sharps_keep_their_spelling()
    {
        var record = new byte[16];
        Assert.True(ChordSymbolParser.TryParse("Gb", out var gFlat));
        LogicChordEncoding.Encode(record, "Gb", gFlat);
        Assert.Equal((1, 6), (record[4], record[5])); // flat, pitch class 6 — Logic shows "Gb" (German "Ges")

        Assert.True(ChordSymbolParser.TryParse("F#m7/C#", out var fSharp));
        LogicChordEncoding.Encode(record, "F#m7/C#", fSharp);
        Assert.Equal((3, 6, 3, 1), (record[4], record[5], record[2], record[3]));
    }

    [Fact]
    public async Task Every_chord_gets_a_region_on_the_chord_track_and_every_section_an_arrangement_marker()
    {
        // 106 chords and 8 sections: more than the template's 17 chord regions and 3 markers.
        var score = Convert(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logic-template-song.abc")), withAccompaniment: false);
        var package = await WriteAsync(score, Flac(48000, 2, 24, TemplateSongSamples));
        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;

        var chordTrack = chunks.Single(c => c.Tag == "MSeq" && c.Class == 23 && c.SequenceName == "Global Harmonies");
        var placements = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == chordTrack.Id).Payload;
        var regionIds = Enumerable.Range(0, placements.Length / 80).Select(i => ReadUInt32(placements, (i * 80) + 32)).ToList();
        Assert.Equal(score.Chords.Count, regionIds.Count);
        Assert.Equal(regionIds.Count, regionIds.Distinct().Count());
        Assert.Equal(34_560u + (2 * (uint)score.Chords[1].StartTicks), ReadUInt32(placements, 80 + 4));
        foreach (var id in regionIds)
        {
            Assert.Single(chunks, c => c.Tag == "MSeq" && c.Class == 23 && c.Id == id);
            Assert.Single(chunks, c => c.Tag == "Trak" && c.Class == 23 && c.Id == id);
            Assert.Single(chunks, c => c.Tag == "EvSq" && c.Class == 23 && c.Id == id);
        }

        var markers = chunks.Single(c => c.Tag == "EvSq" && c.Class == 5 && c.Payload[0] == 0x12).Payload;
        var textIds = Enumerable.Range(0, markers.Length / 48).Select(i => ReadUInt32(markers, (i * 48) + 16)).ToList();
        Assert.Equal(score.Sections.Count, textIds.Count);
        var names = textIds.Select(id => chunks.Single(c => c.Tag == "TxSq" && c.Id == id).Payload).Select(p => System.Text.Encoding.UTF8.GetString(p, 98, p.Length - 99));
        Assert.Equal(score.Sections.Select(s => char.ToUpperInvariant(s.Name[0]) + s.Name[1..]), names);
        Assert.Equal(38_400u + (2 * (uint)score.Sections[1].StartTicks), ReadUInt32(markers, 48 + 4));

        // New objects are listed in both registry tables of the Song chunk.
        var song = chunks.Single(c => c.Tag == "Song").Payload;
        var templateSong = LogicProjectData.Parse(TemplateProjectData).Chunks.Single(c => c.Tag == "Song").Payload;
        // The audio files the project was not given leave the registry, every chord region and marker beyond
        // the template's own joins it, with an entry in each of the two tables.
        var templateAudio = LogicProjectData.Parse(TemplateProjectData).Chunks.Count(c => c.Tag == "AuFl");
        var newObjects = (regionIds.Count - 17) + (textIds.Count - 3) - (templateAudio - 1);
        Assert.Equal(templateSong.Length + (newObjects * (24 + 16)), song.Length);
        foreach (var id in regionIds.Skip(17))
        {
            Assert.Equal(2, CountEntries(song, 23, id));
        }
    }


    [Fact]
    public async Task Sections_become_one_named_region_per_track()
    {
        // The sections of the template song, in order; repeated names are numbered.
        string[] expected =
        [
            "Intro", "Verse 1", "Pre-chorus 1", "Chorus 1", "Interlude 1",
            "Verse 2", "Pre-chorus 2", "Chorus 2", "Interlude 2", "Chorus 3", "Interlude 3",
        ];
        var score = Convert(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logic-template-song.abc")), withAccompaniment: true);

        var package = await WriteAsync(
            score,
            Flac(48000, 2, 24, TemplateSongSamples),
            new LogicProjectOptions { SplitRegionsAtSections = true });

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        var names = chunks.Where(c => c.Tag == "MSeq" && c.Class == 23).ToDictionary(c => c.Id, c => c.SequenceName);
        var placements = Enumerable.Range(0, arrangement.Length / 80)
            .Select(i => new Placement(arrangement[i * 80], ReadUInt32(arrangement, (i * 80) + 4), ReadUInt32(arrangement, (i * 80) + 8), ReadUInt32(arrangement, (i * 80) + 32)))
            .ToList();

        // The audio stays one region at bar 1.
        Assert.Equal(34_560u, Assert.Single(placements, p => p.Head == 0x24).Start);

        var midi = placements.Where(p => p.Head == 0x20).ToList();
        Assert.Equal(midi.Count, midi.Select(p => p.Region).Distinct().Count());

        // Placements of the same track share its track record; the template's regions keep their ids,
        // so each group can be recognized by the voice it grew out of.
        var tracks = midi.GroupBy(p => p.Track).ToList();
        Assert.Equal(TemplateTracks.Length, tracks.Count);
        var sectionStarts = score.Sections.Select(section => 34_560u + (2 * (uint)section.StartTicks)).ToList();

        // Which region id belongs to which part is the template's business, so it is read from there.
        var templateRegions = LogicProjectData.Parse(TemplateProjectData).Chunks
            .Where(c => c.Tag == "MSeq" && c.Class == 23 && TemplateTracks.Contains(c.SequenceName))
            .ToDictionary(c => c.SequenceName, c => c.Id, StringComparer.Ordinal);

        foreach (var (voice, sourceId) in TemplateTracks.Where(t => t != "Drums").Select(t => (t, templateRegions[t])))
        {
            var regions = Assert.Single(tracks, group => group.Any(p => p.Region == sourceId)).OrderBy(p => p.Start).ToList();

            // One region per section the voice plays in, named after it and starting where the section does.
            Assert.All(regions, region => Assert.Contains(region.Start, sectionStarts));
            var labels = regions.Select(region => names[region.Region]).ToList();
            Assert.Equal(labels.OrderBy(l => Array.IndexOf(expected, l)), labels);
            Assert.All(labels, label => Assert.Contains(label, expected));

            // Nothing is lost or duplicated: the regions together hold the notes of the voice, each one
            // positioned inside its own region rather than from the start of the song.
            var sequences = regions.Select(region => chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == region.Region).Payload).ToList();

            // Without a chord pattern the chord track is built from the symbols rather than from a voice.
            var expectedNotes = voice == "Chords" && !score.Voices.Any(v => v.Kind == TrackKind.Chords)
                ? score.Chords.Where(c => c.Symbol is not null).Sum(c => ChordVoicing.GetNotes(c.Symbol!).Count)
                : score.Voice(voice).Notes.Count;
            Assert.Equal(expectedNotes, sequences.Sum(sequence => NoteRecords(sequence).Count));
            foreach (var (region, sequence) in regions.Zip(sequences).Where(pair => NoteRecords(pair.Second).Count > 0))
            {
                var length = ReadUInt32Sequence(chunks, region.Region);
                var positions = NotePositions(sequence);
                Assert.All(positions, position => Assert.InRange(position, 38_400u, 38_400u + length));
            }

            foreach (var id in regions.Select(r => r.Region).Where(id => id > 44))
            {
                Assert.Single(chunks, c => c.Tag == "MSeq" && c.Class == 23 && c.Id == id);
                Assert.Single(chunks, c => c.Tag == "Trak" && c.Class == 23 && c.Id == id);
                Assert.Single(chunks, c => c.Tag == "EvSq" && c.Class == 23 && c.Id == id);
                Assert.Equal(2, CountEntries(chunks.Single(c => c.Tag == "Song").Payload, 23, id));
            }
        }
    }

    [Fact]
    public async Task Without_splitting_every_track_keeps_its_single_region()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273));

        var arrangement = LogicProjectData.Parse(package.ProjectData).Chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        Assert.Equal(TemplateTracks.Length + 1, arrangement.Length / 80);
    }

    private readonly record struct Placement(byte Head, uint Start, uint Track, uint Region);

    private static uint ReadUInt32Sequence(List<LogicChunk> chunks, uint regionId)
    {
        var region = chunks.Single(c => c.Tag == "MSeq" && c.Class == 23 && c.Id == regionId);
        return ReadUInt32(region.Payload, region.SequenceLengthOffset);
    }

    private static List<uint> NotePositions(byte[] sequence) =>
        Enumerable.Range(0, sequence.Length / 16 - 1)
            .Where(i => (sequence[i * 16] & 0xF0) == 0x90)
            .Select(i => ReadUInt32(sequence, (i * 16) + 4))
            .ToList();


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_count_in_moves_the_audio_behind_it(bool splitSections)
    {
        // The MIDI regions still start at bar 1 and carry the silent bars inside them; the audio has no
        // count-in of its own, so it has to begin where the music does.
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { Bass = new BassOptions(), CountIn = new CountInOptions { Bars = 2 } },
        };
        var converted = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);
        Assert.True(converted.Success, string.Join(Environment.NewLine, converted.Diagnostics));
        var score = converted.Score!;
        Assert.Equal(2 * Bar, score.CountInTicks);

        var package = await WriteAsync(
            score,
            Flac(48000, 2, 24, 1_047_273),
            new LogicProjectOptions { SplitRegionsAtSections = splitSections });

        var arrangement = LogicProjectData.Parse(package.ProjectData).Chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        var placements = Enumerable.Range(0, arrangement.Length / 80)
            .Select(i => (Head: arrangement[i * 80], Start: ReadUInt32(arrangement, (i * 80) + 4)))
            .ToList();

        // Logic counts at 960 ticks per quarter, so two 4/4 bars are 7680 ticks past bar 1.
        const uint countIn = 7_680;
        Assert.Equal(34_560u + countIn, Assert.Single(placements, p => p.Head == 0x24).Start);

        var midi = placements.Where(p => p.Head == 0x20).ToList();
        Assert.All(midi, p => Assert.True(p.Start >= 34_560u, "a region starts before bar 1"));
        if (splitSections)
        {
            // The lead-in is a stretch of its own before the first section, so the clicks get a region at
            // bar 1 while the music begins behind it.
            Assert.Contains(midi, p => p.Start == 34_560u);
            Assert.Contains(midi, p => p.Start == 34_560u + countIn);
        }
        else
        {
            Assert.All(midi, p => Assert.Equal(34_560u, p.Start));
        }
    }

    [Fact]
    public async Task A_count_in_is_left_out_of_the_audio_length_check()
    {
        // Without this the silent bars would look like a mismatch between score and recording.
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { CountIn = new CountInOptions { Bars = 4 } },
        };
        var score = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options).Score!;

        var result = await new LogicProjectWriter().WriteAsync(score, new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 1_047_273))), new MemorySink());

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.AudioLengthMismatch);
    }


    [Fact]
    public async Task Tracks_are_named_after_the_parts_they_carry()
    {
        // The template's tracks are named after the instruments chosen in it ("Studio Grand"), which says
        // nothing about the part once the regions carry the section names instead of the track name.
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: true);

        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273));

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        var names = TrackNames(chunks);

        // Every track says what it carries: the audio ones what they play, the MIDI ones their part.
        Assert.Contains("Mix", names);
        Assert.Equal(TemplateTracks.Order(), names.Intersect(TemplateTracks).Order());
        Assert.DoesNotContain(names, name => name.Contains("vocals_", StringComparison.Ordinal)); // no file names left

        // In the template they are named after the instrument chosen for them and the file dropped on them.
        var templateNames = TrackNames(LogicProjectData.Parse(TemplateProjectData).Chunks);
        Assert.Contains("audio_1", templateNames);
        Assert.Contains("Studio Grand", templateNames);
    }

    /// <summary>
    /// The channel strips of the arrangement's tracks, in track order. The list also holds the output bus,
    /// which points at no environment object of its own class and is left out here as the writer leaves it.
    /// </summary>
    private static List<string> TrackNames(List<LogicChunk> chunks)
    {
        var environment = chunks.Where(c => c.Tag == "Envi" && c.Class == 20 && c.Payload.Length > 160).ToDictionary(c => c.Id);
        return chunks
            .Where(c => c.Tag == "Trak" && c.Class == 23 && c.Id == 4 && c.Payload.Length == 58 && c.Payload[0] == 1)
            .Select(c => ReadUInt32(c.Payload, 8))
            .Where(environment.ContainsKey)
            .Select(id => environment[id].Payload)
            .Select(p => System.Text.Encoding.UTF8.GetString(p, 160, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(158, 2))))
            .ToList();
    }

    [Fact]
    public async Task A_voice_the_template_has_no_track_for_is_reported_with_the_tracks_it_does_have()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { Doubling = new DoublingOptions { VoiceId = "Ins" } },
        };
        var converted = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);

        var result = await new LogicProjectWriter().WriteAsync(converted.Score!, new LogicAudio(null), new MemorySink());

        var warning = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.LogicTemplateLimitation);
        Assert.Contains("'Ins 8vb'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Vocal 8vb", warning.Message, StringComparison.Ordinal); // the tracks it does have
    }

    [Theory]
    [InlineData(60, 0x0000)]
    [InlineData(72, 0x2080)]
    [InlineData(80, 0x4100)]
    [InlineData(96, 0x8200)]
    [InlineData(100, 0x9200)]
    [InlineData(110, 0xBA80)]
    public void High_resolution_velocity_matches_logic(int velocity, int expected)
    {
        Assert.Equal(expected, LogicProjectWriter.HighResolutionVelocity(velocity));
    }

    [Fact]
    public async Task Zip_sink_nests_the_package_and_stores_audio_uncompressed()
    {
        using var zip = new MemoryStream();
        using (var sink = new ZipLogicPackageSink(zip, "My Song"))
        {
            await new LogicProjectWriter().WriteAsync(Convert(File.ReadAllText(SamplePath), false), new LogicAudio(new MemoryStream(Flac(48000, 2, 24, 1_047_273))), sink);
        }

        using var archive = new ZipArchive(new MemoryStream(zip.ToArray()));
        Assert.All(archive.Entries, e => Assert.StartsWith("My Song.logicx/", e.FullName, StringComparison.Ordinal));
        var audio = archive.GetEntry("My Song.logicx/Media/Audio Files/audio.flac")!;
        Assert.Equal(audio.Length, audio.CompressedLength);
    }

    private static ScoreDocument Convert(string abc, bool withAccompaniment, bool splitDrums = true)
    {
        // With accompaniment means every track the Logic template carries, which is how the template was made.
        var options = new ConversionOptions
        {
            Arrangement = withAccompaniment
                ? new ArrangementOptions
                {
                    Bass = new BassOptions(),
                    Drums = new DrumOptions { SeparateTracks = splitDrums },
                    GuideTones = new GuideToneOptions(),
                    Doubling = new DoublingOptions(),
                }
                : new ArrangementOptions(),
        };
        var result = new ScoreConverter().Convert(abc, options);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Score!;
    }

    private static Task<(byte[] ProjectData, IReadOnlyDictionary<string, byte[]> Files)> WriteAsync(
        ScoreDocument score,
        byte[] flac,
        LogicProjectOptions? options = null) =>
        WriteAsync(score, new LogicAudio(new MemoryStream(flac)), options);

    private static async Task<(byte[] ProjectData, IReadOnlyDictionary<string, byte[]> Files)> WriteAsync(
        ScoreDocument score,
        LogicAudio audio,
        LogicProjectOptions? options = null)
    {
        var sink = new MemorySink();
        var result = await new LogicProjectWriter().WriteAsync(score, audio, sink, options);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var files = sink.Files.ToDictionary(f => f.Key, f => f.Value.ToArray());
        return (files[LogicTemplate.ProjectDataPath], files);
    }

    /// <summary>The sample count an audio file object stores, behind the tag that names its format.</summary>
    private static long SampleCount(LogicChunk file)
    {
        var format = Math.Max(file.Payload.AsSpan().LastIndexOf("CaLf"u8), file.Payload.AsSpan().LastIndexOf("EVAW"u8));
        return (long)BinaryPrimitives.ReadUInt64LittleEndian(file.Payload.AsSpan(format + 12));
    }

    /// <summary>A WAVE header of the usual chunks, followed by as much silence as it says it has.</summary>
    private static byte[] Wave(int sampleRate, int channels, int bitsPerSample, long samples)
    {
        var bytesPerFrame = channels * (bitsPerSample / 8);
        var data = (int)(samples * bytesPerFrame);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + data));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(sampleRate * bytesPerFrame));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)bytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), (ushort)bitsPerSample);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)data);
        return [.. header, .. new byte[Math.Min(data, 512)]];
    }

    /// <summary>A FLAC header (STREAMINFO) followed by filler bytes standing in for the audio frames.</summary>
    private static byte[] Flac(int sampleRate, int channels, int bitsPerSample, long samples)
    {
        var data = new byte[FlacStreamInfo.HeaderLength + 256];
        "fLaC"u8.CopyTo(data);
        data[4] = 0x80; // last metadata block, type STREAMINFO
        data[7] = 34;
        var body = data.AsSpan(8, 34);
        body[10] = (byte)(sampleRate >> 12);
        body[11] = (byte)(sampleRate >> 4);
        body[12] = (byte)(((sampleRate & 0x0F) << 4) | ((channels - 1) << 1) | ((bitsPerSample - 1) >> 4));
        body[13] = (byte)((((bitsPerSample - 1) & 0x0F) << 4) | (int)((samples >> 32) & 0x0F));
        BinaryPrimitives.WriteUInt32BigEndian(body[14..], (uint)samples);
        for (var i = FlacStreamInfo.HeaderLength; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        return data;
    }

    private static Dictionary<string, byte[]> RegionEvents(byte[] projectData)
    {
        var chunks = LogicProjectData.Parse(projectData).Chunks;
        var names = chunks.Where(c => c.Tag == "MSeq" && c.Class == 23).ToDictionary(c => c.Id, c => c.SequenceName);
        return chunks
            .Where(c => c.Tag == "EvSq" && c.Class == 23 && names.TryGetValue(c.Id, out var n) && TemplateTracks.Contains(n))
            .ToDictionary(c => names[c.Id], c => c.Payload);
    }

    private static List<string> NoteRecords(byte[] sequence) =>
        Enumerable.Range(0, sequence.Length / 16 - 1)
            .Where(i => (sequence[i * 16] & 0xF0) == 0x90)
            .Select(i => System.Convert.ToHexString(sequence, i * 16, 32))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The note records of a region with their positions counted from its first note, so that a region Logic
    /// placed where its notes begin compares with one that starts at bar 1.
    /// </summary>
    private static List<string> NotesFromFirst(byte[] sequence)
    {
        var records = Enumerable.Range(0, sequence.Length / 16 - 1)
            .Where(i => (sequence[i * 16] & 0xF0) == 0x90)
            .Select(i => sequence.AsSpan(i * 16, 32).ToArray())
            .ToList();
        if (records.Count == 0)
        {
            return [];
        }

        var first = records.Min(r => ReadUInt32(r, 4));
        foreach (var record in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), ReadUInt32(record, 4) - first);
        }

        return records.Select(System.Convert.ToHexString).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The chord data records of the template's chord regions, in the order of the chord track.</summary>
    private static List<byte[]> TemplateChordRecords()
    {
        var chunks = LogicProjectData.Parse(TemplateProjectData).Chunks;
        var track = chunks.Single(c => c.Tag == "MSeq" && c.Class == 23 && c.SequenceName == "Global Harmonies");
        var placements = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == track.Id).Payload;
        return Enumerable.Range(0, placements.Length / 80)
            .Select(i => ReadUInt32(placements, (i * 80) + 32))
            .Select(id => chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == id).Payload[48..64])
            .ToList();
    }

    private static int CountEntries(byte[] song, uint klass, uint id)
    {
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(key, klass);
        BinaryPrimitives.WriteUInt32LittleEndian(key[4..], id);
        var count = 0;
        for (var i = song.AsSpan().IndexOf(key); i >= 0;)
        {
            count++;
            var next = song.AsSpan(i + 1).IndexOf(key);
            i = next < 0 ? -1 : i + 1 + next;
        }

        return count;
    }

    private static uint ReadUInt32(byte[] buffer, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));

    private static int CountOccurrences(byte[] buffer, byte[] pattern)
    {
        var count = 0;
        for (var start = 0; start < buffer.Length;)
        {
            var index = buffer.AsSpan(start).IndexOf(pattern);
            if (index < 0)
            {
                break;
            }

            count++;
            start += index + 1;
        }

        return count;
    }

    private static int CountUInt64(byte[] buffer, long value)
    {
        Span<byte> pattern = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pattern, (ulong)value);
        var count = 0;
        var start = 0;
        int index;
        while ((index = buffer.AsSpan(start).IndexOf(pattern)) >= 0)
        {
            count++;
            start += index + 1;
        }

        return count;
    }

    private static int CountUInt32(byte[] buffer, uint value)
    {
        Span<byte> pattern = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, value);
        var count = 0;
        var start = 0;
        int index;
        while ((index = buffer.AsSpan(start).IndexOf(pattern)) >= 0)
        {
            count++;
            start += index + 1;
        }

        return count;
    }

    private static List<string> PlistValues(byte[] plist, string key)
    {
        var dict = XDocument.Parse(System.Text.Encoding.UTF8.GetString(plist)).Root!.Element("dict")!;
        var array = dict.Elements("key").First(k => k.Value == key).ElementsAfterSelf().First();
        return [.. array.Elements("string").Select(e => e.Value)];
    }

    private static string PlistValue(byte[] plist, string key)
    {
        var dict = XDocument.Parse(System.Text.Encoding.UTF8.GetString(plist)).Root!.Element("dict")!;
        return dict.Elements("key").First(k => k.Value == key).ElementsAfterSelf().First().Value;
    }

    private sealed class MemorySink : ILogicPackageSink
    {
        public Dictionary<string, MemoryStream> Files { get; } = [];

        public Stream CreateFile(string relativePath) => Files[relativePath] = new MemoryStream();
    }
}
