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
    private const int TemplateSongSamples = 11_408_576; // audio.flac of the template song, 48 kHz

    private static readonly byte[] TemplateProjectData = LogicTemplate.Default.Files[LogicTemplate.ProjectDataPath];

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
        foreach (var region in new[] { "Ins", "Bass", "Drums" })
        {
            Assert.True(original[region].SequenceEqual(generated[region]), $"{region} differs from Logic's encoding");
        }

        // Logic additionally stores the chord names as text events; the notes themselves must match.
        Assert.Equal(NoteRecords(original["Chords"]), NoteRecords(generated["Chords"]));
        // The template's vocal region starts at bar 8 (relative positions); ours at bar 1, so only the notes' content compares.
        Assert.Equal(NoteRecords(original["Vocal"]).Count, NoteRecords(generated["Vocal"]).Count);
    }

    [Fact]
    public async Task Tempo_meter_lengths_and_audio_are_set_from_the_score()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: false); // 88 BPM, 4/4, 8 bars
        var package = await WriteAsync(score, Flac(48000, 2, 24, 1_047_273));

        var chunks = LogicProjectData.Parse(package.ProjectData).Chunks;
        const uint songTicks = 8 * 3840;

        // Every copy of the template's tempo in the Song chunk is replaced (how many depends on the template).
        var templateSong = LogicProjectData.Parse(TemplateProjectData).Chunks.Single(c => c.Tag == "Song").Payload;
        Assert.Equal(CountUInt32(templateSong, 1_080_000), CountUInt32(chunks.Single(c => c.Tag == "Song").Payload, 880_000));
        Assert.Equal(880_000u, ReadUInt32(chunks.Single(c => c.Tag == "EvSq" && c.Class == 3).Payload, 16));
        Assert.Equal(0, CountUInt32(package.ProjectData, 1_080_000));

        var sequences = chunks.Where(c => c.Tag == "MSeq" && c.Class == 23).ToList();
        foreach (var sequence in sequences.Where(s => s.Id == 4 || s.SequenceName is "Vocal" or "Ins" or "Chords" or "Bass" or "Drums"))
        {
            Assert.Equal(songTicks, ReadUInt32(sequence.Payload, sequence.SequenceLengthOffset));
        }

        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        var placements = Enumerable.Range(0, arrangement.Length / 16 - 1)
            .Select(i => i * 16)
            .Where(i => arrangement[i] is 0x20 or 0x24 && arrangement[i + 23] == 0x89)
            .Select(i => ReadUInt32(arrangement, i + 4))
            .ToList();
        Assert.Equal(6, placements.Count);
        Assert.All(placements, p => Assert.Equal(34_560u, p));

        var audioFile = chunks.Single(c => c.Tag == "AuFl").Payload;
        Assert.Equal(1_047_273UL, BinaryPrimitives.ReadUInt64LittleEndian(audioFile.AsSpan(498)));
        Assert.Equal(48_000u, ReadUInt32(audioFile, 506));
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
    public async Task Without_audio_the_project_keeps_an_empty_audio_track()
    {
        var score = Convert(File.ReadAllText(SamplePath), withAccompaniment: false);
        var sink = new MemorySink();

        var result = await new LogicProjectWriter().WriteAsync(score, flacAudio: null, sink);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Null(result.Audio);
        Assert.DoesNotContain(LogicTemplate.AudioPath, sink.Files.Keys);
        Assert.Contains(LogicTemplate.ProjectDataPath, sink.Files.Keys);

        var projectData = sink.Files[LogicTemplate.ProjectDataPath].ToArray();
        var chunks = LogicProjectData.Parse(projectData).Chunks;
        var arrangement = chunks.Single(c => c.Tag == "EvSq" && c.Class == 23 && c.Id == 4).Payload;
        var heads = Enumerable.Range(0, arrangement.Length / 80).Select(i => arrangement[i * 80]).ToList();
        Assert.Equal(Enumerable.Repeat((byte)0x20, 5), heads); // the five MIDI regions, without the audio region

        // Logic would report the template's audio file as missing, so file and region are gone, registry included.
        Assert.DoesNotContain(chunks, c => c.Tag is "AuFl" or "AuRg");
        var song = chunks.Single(c => c.Tag == "Song").Payload;
        var templateSong = LogicProjectData.Parse(TemplateProjectData).Chunks.Single(c => c.Tag == "Song").Payload;
        Assert.Equal(2, CountEntries(templateSong, 11, 0)); // one entry in each of the two registry tables
        Assert.Equal(0, CountEntries(song, 11, 0));
        Assert.Equal(templateSong.Length - 24 - 16, song.Length);

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

        var result = await new LogicProjectWriter().WriteAsync(Convert(File.ReadAllText(SamplePath), false), new MemoryStream(audio), sink);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(sink.Files);
    }

    [Fact]
    public async Task Audio_of_a_different_length_is_reported()
    {
        var result = await new LogicProjectWriter().WriteAsync(Convert(File.ReadAllText(SamplePath), false), new MemoryStream(Flac(48000, 2, 24, 48_000 * 120)), new MemorySink());

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
        var newObjects = (regionIds.Count - 17) + (textIds.Count - 3);
        Assert.Equal(templateSong.Length + (newObjects * (24 + 16)), song.Length);
        foreach (var id in regionIds.Skip(17))
        {
            Assert.Equal(2, CountEntries(song, 23, id));
        }
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
            await new LogicProjectWriter().WriteAsync(Convert(File.ReadAllText(SamplePath), false), new MemoryStream(Flac(48000, 2, 24, 1_047_273)), sink);
        }

        using var archive = new ZipArchive(new MemoryStream(zip.ToArray()));
        Assert.All(archive.Entries, e => Assert.StartsWith("My Song.logicx/", e.FullName, StringComparison.Ordinal));
        var audio = archive.GetEntry("My Song.logicx/Media/Audio Files/audio.flac")!;
        Assert.Equal(audio.Length, audio.CompressedLength);
    }

    private static ScoreDocument Convert(string abc, bool withAccompaniment)
    {
        var options = new ConversionOptions
        {
            Arrangement = withAccompaniment
                ? new ArrangementOptions { Bass = new BassOptions(), Drums = new DrumOptions() }
                : new ArrangementOptions(),
        };
        var result = new ScoreConverter().Convert(abc, options);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Score!;
    }

    private static async Task<(byte[] ProjectData, IReadOnlyDictionary<string, byte[]> Files)> WriteAsync(ScoreDocument score, byte[] flac)
    {
        var sink = new MemorySink();
        var result = await new LogicProjectWriter().WriteAsync(score, new MemoryStream(flac), sink);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var files = sink.Files.ToDictionary(f => f.Key, f => f.Value.ToArray());
        return (files[LogicTemplate.ProjectDataPath], files);
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
            .Where(c => c.Tag == "EvSq" && c.Class == 23 && names.TryGetValue(c.Id, out var n) && n is "Vocal" or "Ins" or "Chords" or "Bass" or "Drums")
            .ToDictionary(c => names[c.Id], c => c.Payload);
    }

    private static List<string> NoteRecords(byte[] sequence) =>
        Enumerable.Range(0, sequence.Length / 16 - 1)
            .Where(i => (sequence[i * 16] & 0xF0) == 0x90)
            .Select(i => System.Convert.ToHexString(sequence, i * 16, 32))
            .Order(StringComparer.Ordinal)
            .ToList();

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
