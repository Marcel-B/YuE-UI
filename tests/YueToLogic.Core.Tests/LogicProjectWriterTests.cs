using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
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
