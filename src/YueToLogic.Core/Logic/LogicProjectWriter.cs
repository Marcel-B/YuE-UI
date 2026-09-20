using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Logic;

public interface ILogicProjectWriter
{
    /// <summary>
    /// Writes a Logic Pro package with the score's tracks into <paramref name="sink"/>, and the YuE audio
    /// (<c>audio.flac</c>) if <paramref name="flacAudio"/> is given; without it the project keeps an empty audio
    /// track. Nothing is written if the result reports an error.
    /// </summary>
    Task<LogicProjectResult> WriteAsync(
        ScoreDocument score,
        Stream? flacAudio,
        ILogicPackageSink sink,
        LogicProjectOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record LogicProjectOptions
{
    /// <summary>Name shown for the project alternative; the package file name is chosen by the host.</summary>
    public string ProjectName { get; set; } = "YuE";

    public int MelodyVelocity { get; set; } = 96;

    public int ChordVelocity { get; set; } = 72;
}

public sealed record LogicProjectResult(bool Success, FlacStreamInfo? Audio, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Creates a Logic Pro project by filling a template saved by Logic (see <see cref="LogicTemplate"/>): it replaces the
/// notes of the five MIDI regions, their length, tempo, meter, project length and the audio file, and writes the
/// chords to the chord track and the sections as arrangement markers. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Logic's project format is undocumented. The offsets below were derived from projects saved by Logic Pro 12.3.1 and
/// verified by opening generated projects in Logic; a future Logic version may require a new template.
/// </remarks>
public sealed partial class LogicProjectWriter : ILogicProjectWriter
{
    private readonly LogicTemplate template;
    private readonly TimeProvider timeProvider;

    /// <summary>The template project runs at 48 kHz; Logic does not resample audio regions on playback.</summary>
    public const int RequiredSampleRate = 48000;

    private const int LogicTicksPerQuarterNote = 960;

    // Positions: note events are stored relative to their region with 38400 as the region start;
    // the arrangement stores bar 1 as 34560.
    private const uint EventOrigin = 38400;
    private const uint ArrangementBar1 = 34560;

    // Chunk classes and ids of the structures that are patched.
    private const ushort SignatureListClass = 1;
    private const ushort TempoListClass = 3;
    private const ushort ArrangementClass = 23;
    private const uint RootSequenceId = 4;

    /// <summary>A region in the arrangement: five 16-byte records, the first one 0x24 for audio and 0x20 for MIDI.</summary>
    private const int PlacementLength = 80;
    private const byte AudioPlacement = 0x24;

    // Offsets inside the audio file (AuFl) and audio region (AuRg) payloads.
    private const int AudioFileSamplesOffset = 498;
    private const int AudioFileSampleRateOffset = 506;
    private const int AudioFileChannelsOffset = 510;
    private const int AudioRegionSamplesOffset = 22;

    private static readonly byte[] SequenceTerminator = Convert.FromHexString("F1000000FFFFFF3F0000000000000000");

    /// <summary>MIDI regions of the template and the channel Logic stored their events on.</summary>
    private static readonly (string Region, int Channel)[] Regions =
    [
        ("Vocal", 0),
        ("Ins", 1),
        ("Chords", 2),
        ("Bass", 3),
        ("Drums", 9),
    ];

    public LogicProjectWriter()
        : this(LogicTemplate.Default)
    {
    }

    /// <param name="timeProvider">Clock for the time-based ids of objects the writer creates; the system clock by default.</param>
    public LogicProjectWriter(LogicTemplate template, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        this.template = template;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LogicProjectResult> WriteAsync(
        ScoreDocument score,
        Stream? flacAudio,
        ILogicPackageSink sink,
        LogicProjectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(sink);
        options ??= new LogicProjectOptions();
        var diagnostics = new DiagnosticBag();

        var audioHeader = new byte[FlacStreamInfo.HeaderLength];
        var headerLength = 0;
        FlacStreamInfo? audio = null;
        if (flacAudio is not null)
        {
            headerLength = await flacAudio.ReadAtLeastAsync(audioHeader, audioHeader.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (!FlacStreamInfo.TryParse(audioHeader.AsSpan(0, headerLength), out audio))
            {
                diagnostics.Error(DiagnosticCodes.InvalidAudio, "The audio file is not a FLAC file; upload the audio.flac written by YuE.");
                return new LogicProjectResult(false, null, diagnostics.ToList());
            }

            if (audio!.SampleRate != RequiredSampleRate)
            {
                diagnostics.Error(
                    DiagnosticCodes.UnsupportedSampleRate,
                    Invariant($"The audio has {audio.SampleRate} Hz; the Logic project needs {RequiredSampleRate} Hz (YuE writes 48 kHz)."));
                return new LogicProjectResult(false, audio, diagnostics.ToList());
            }

            CheckAudio(score, audio, diagnostics);
        }

        var events = CollectEvents(score, options, diagnostics);
        var projectData = BuildProjectData(score, audio, events, diagnostics);

        await WriteFileAsync(sink, LogicTemplate.ProjectDataPath, projectData, cancellationToken).ConfigureAwait(false);
        await WriteFileAsync(sink, LogicTemplate.MetaDataPath, BuildMetaData(score, audio is not null), cancellationToken).ConfigureAwait(false);
        await WriteFileAsync(sink, LogicTemplate.ProjectInformationPath, BuildProjectInformation(options.ProjectName), cancellationToken).ConfigureAwait(false);
        foreach (var (path, content) in template.Files)
        {
            if (path is not (LogicTemplate.ProjectDataPath or LogicTemplate.MetaDataPath or LogicTemplate.ProjectInformationPath))
            {
                await WriteFileAsync(sink, path, content, cancellationToken).ConfigureAwait(false);
            }
        }

        if (flacAudio is not null)
        {
            await using var target = sink.CreateFile(LogicTemplate.AudioPath);
            await target.WriteAsync(audioHeader.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
            await flacAudio.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return new LogicProjectResult(true, audio, diagnostics.ToList());
    }

    // ---- Notes ------------------------------------------------------------------------------------

    private readonly record struct LogicNote(long Start, long Length, int Pitch, int Velocity);

    private static Dictionary<string, List<LogicNote>> CollectEvents(ScoreDocument score, LogicProjectOptions options, DiagnosticBag diagnostics)
    {
        var events = Regions.ToDictionary(r => r.Region, _ => new List<LogicNote>(), StringComparer.Ordinal);
        long Ticks(long scoreTicks) => ToLogicTicks(scoreTicks, score.TicksPerQuarterNote);

        foreach (var voice in score.Voices)
        {
            var region = voice.Kind switch
            {
                TrackKind.Bass => "Bass",
                TrackKind.Drums => "Drums",
                _ => voice.Id,
            };
            if (!events.TryGetValue(region, out var target) || region is "Chords")
            {
                diagnostics.Warning(
                    DiagnosticCodes.LogicTemplateLimitation,
                    $"The Logic template has no track for voice '{voice.Id}'; it is only in the MIDI file.");
                continue;
            }

            target.AddRange(voice.Notes.Select(n => new LogicNote(
                Ticks(n.StartTicks), Ticks(n.DurationTicks), n.NoteNumber, n.Velocity ?? options.MelodyVelocity)));
        }

        foreach (var chord in score.Chords.Where(c => c.Symbol is not null))
        {
            events["Chords"].AddRange(ChordVoicing.GetNotes(chord.Symbol!).Select(pitch => new LogicNote(
                Ticks(chord.StartTicks), Ticks(chord.DurationTicks), pitch, options.ChordVelocity)));
        }

        foreach (var list in events.Values)
        {
            list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        }

        return events;
    }

    /// <summary>A note is two 16-byte records: note-on with position, velocity and pitch, then its length.</summary>
    private static byte[] EncodeSequence(IReadOnlyList<LogicNote> notes, int channel)
    {
        var data = new byte[(notes.Count * 32) + SequenceTerminator.Length];
        var span = data.AsSpan();
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            var velocity = Math.Clamp(note.Velocity, 1, 127);
            var record = span.Slice(i * 32, 32);
            record[0] = (byte)(0x90 | channel);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], checked((uint)(EventOrigin + note.Start)));
            BinaryPrimitives.WriteUInt16LittleEndian(record[9..], HighResolutionVelocity(velocity));
            record[11] = (byte)velocity;
            record[12] = (byte)Math.Clamp(note.Pitch, 0, 127);
            record[15] = 0x01;
            record[16 + 7] = 0x89;
            BinaryPrimitives.WriteUInt32LittleEndian(record[(16 + 12)..], checked((uint)Math.Max(1, note.Length)));
        }

        SequenceTerminator.CopyTo(span[(notes.Count * 32)..]);
        return data;
    }

    /// <summary>
    /// Bytes 9–10 of a note record: 0 up to velocity 64, above that 128 · (8·(v−64) + ⌊(v−64)/8⌋).
    /// Derived from the values Logic stores for all 58 velocities found in real projects.
    /// </summary>
    internal static ushort HighResolutionVelocity(int velocity)
    {
        var above = velocity - 64;
        return above <= 0 ? (ushort)0 : (ushort)(128 * ((8 * above) + (above / 8)));
    }

    // ---- ProjectData ------------------------------------------------------------------------------

    private byte[] BuildProjectData(ScoreDocument score, FlacStreamInfo? audio, Dictionary<string, List<LogicNote>> events, DiagnosticBag diagnostics)
    {
        var project = LogicProjectData.Parse(template.Files[LogicTemplate.ProjectDataPath]);
        var chunks = project.Chunks;
        var songTicks = checked((uint)ToLogicTicks(score.LengthTicks, score.TicksPerQuarterNote));

        // Regions: set their length, remember which event sequence belongs to which track.
        var regionById = new Dictionary<uint, string>();
        foreach (var sequence in chunks.Where(c => c.Tag == "MSeq" && c.Class == ArrangementClass))
        {
            var name = sequence.SequenceName;
            if (events.ContainsKey(name) || sequence.Id == RootSequenceId)
            {
                // The root sequence's length is the project length (end marker).
                WriteUInt32(sequence.Payload, sequence.SequenceLengthOffset, songTicks);
                if (sequence.Id != RootSequenceId)
                {
                    regionById[sequence.Id] = name;
                }
            }
        }

        foreach (var (region, _) in Regions)
        {
            if (!regionById.ContainsValue(region))
            {
                throw new InvalidOperationException($"The Logic template has no MIDI region '{region}'.");
            }
        }

        foreach (var sequence in chunks.Where(c => c.Tag == "EvSq" && c.Class == ArrangementClass))
        {
            if (regionById.TryGetValue(sequence.Id, out var region))
            {
                var channel = Regions.First(r => r.Region == region).Channel;
                sequence.Payload = EncodeSequence(events[region], channel);
            }
            else if (sequence.Id == RootSequenceId)
            {
                PlaceRegionsAtBarOne(sequence.Payload);
                if (audio is null)
                {
                    sequence.Payload = WithoutAudioRegion(sequence.Payload);
                }
            }
        }

        SetTempo(chunks, score.TempoBpm);
        WriteSignatures(chunks, score);
        var removed = audio is null ? RemoveAudioObjects(chunks) : [];
        if (audio is not null)
        {
            SetAudio(chunks, audio);
        }

        var created = WriteChordTrack(chunks, score);
        created.AddRange(WriteArrangementMarkers(chunks, score));
        var song = chunks.Single(c => c.Tag == "Song");
        if (removed.Count > 0)
        {
            song.Payload = LogicObjectRegistry.Remove(song.Payload, removed);
        }

        if (created.Count > 0)
        {
            song.Payload = LogicObjectRegistry.Register(song.Payload, created, timeProvider.GetUtcNow(), Random.Shared);
        }

        return project.Serialize();
    }

    /// <summary>The arrangement lists each region as five records; the first holds its start position.</summary>
    private static void PlaceRegionsAtBarOne(byte[] arrangement)
    {
        for (var i = 0; i + 32 <= arrangement.Length; i += 16)
        {
            if (arrangement[i] is 0x20 or 0x24 && arrangement[i + 16 + 7] == 0x89)
            {
                WriteUInt32(arrangement, i + 4, ArrangementBar1);
            }
        }
    }

    /// <summary>
    /// Drops the audio file and its region from a project written without audio; without this Logic would report
    /// the audio file of the template as missing when the project is opened. The audio track itself stays, empty.
    /// </summary>
    /// <returns>The removed objects, which have to leave the registry as well.</returns>
    private static List<(uint Class, uint Id)> RemoveAudioObjects(List<LogicChunk> chunks)
    {
        var audioObjects = chunks.Where(c => c.Tag is "AuFl" or "AuRg").ToList();
        var removed = audioObjects.Select(c => ((uint)c.Class, c.Id)).Distinct().ToList();
        chunks.RemoveAll(audioObjects.Contains);
        return removed;
    }

    /// <summary>The arrangement without the audio region, so that the audio track opens empty.</summary>
    private static byte[] WithoutAudioRegion(byte[] arrangement)
    {
        for (var i = 0; i + PlacementLength <= arrangement.Length; i += PlacementLength)
        {
            if (arrangement[i] == AudioPlacement && arrangement[i + 16 + 7] == 0x89)
            {
                return [.. arrangement.AsSpan(0, i), .. arrangement.AsSpan(i + PlacementLength)];
            }
        }

        return arrangement;
    }

    /// <summary>
    /// Tempo is stored as BPM · 10000 in the tempo list and in several places of the Song chunk. The list is reduced
    /// to its first event (two records), since the score has a single tempo.
    /// </summary>
    private static void SetTempo(List<LogicChunk> chunks, double bpm)
    {
        var tempoList = chunks.Single(c => c.Tag == "EvSq" && c.Class == TempoListClass);
        var oldTempo = BinaryPrimitives.ReadUInt32LittleEndian(tempoList.Payload.AsSpan(16, 4));
        tempoList.Payload = [.. tempoList.Payload.AsSpan(0, 32), .. SequenceTerminator];
        var newTempo = (uint)Math.Round(bpm * 10000);
        foreach (var chunk in new[] { tempoList, chunks.Single(c => c.Tag == "Song") })
        {
            ReplaceUInt32(chunk.Payload, oldTempo, newTempo);
        }
    }

    private static void SetAudio(List<LogicChunk> chunks, FlacStreamInfo audio)
    {
        var file = chunks.Single(c => c.Tag == "AuFl").Payload;
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(AudioFileSamplesOffset), (ulong)audio.TotalSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(AudioFileSampleRateOffset), (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(AudioFileChannelsOffset), (ushort)audio.Channels);

        // Forget the folder the template's audio came from, so Logic looks inside the package.
        var folder = file.AsSpan().IndexOf("PMOC"u8);
        if (folder >= 0)
        {
            var start = folder + 4;
            var end = file.AsSpan(start).IndexOf((byte)0);
            file.AsSpan(start, end < 0 ? file.Length - start : end).Clear();
        }

        var region = chunks.Single(c => c.Tag == "AuRg").Payload;
        BinaryPrimitives.WriteUInt64LittleEndian(region.AsSpan(AudioRegionSamplesOffset), (ulong)audio.TotalSamples);
    }

    private static void CheckAudio(ScoreDocument score, FlacStreamInfo audio, DiagnosticBag diagnostics)
    {
        if (audio.Channels != 2)
        {
            diagnostics.Warning(DiagnosticCodes.InvalidAudio, Invariant($"The audio has {audio.Channels} channel(s); the template's audio track is stereo."));
        }

        var difference = Math.Abs(audio.DurationSeconds - score.DurationSeconds);
        if (difference > Math.Max(5, score.DurationSeconds * 0.05))
        {
            diagnostics.Warning(
                DiagnosticCodes.AudioLengthMismatch,
                Invariant($"The audio lasts {audio.DurationSeconds:0.0} s but the score {score.DurationSeconds:0.0} s; is it the audio.flac from the same YuE run?"));
        }
    }

    // ---- Property lists ---------------------------------------------------------------------------

    private byte[] BuildMetaData(ScoreDocument score, bool withAudio)
    {
        var document = LoadPropertyList(template.Files[LogicTemplate.MetaDataPath]);
        var root = document.Root!.Element("dict")!;
        SetValue(root, "BeatsPerMinute", new XElement("real", score.TempoBpm.ToString("0.####", CultureInfo.InvariantCulture)));
        SetValue(root, "SongSignatureNumerator", new XElement("integer", score.TimeSignatures[0].Numerator));
        SetValue(root, "SongSignatureDenominator", new XElement("integer", score.TimeSignatures[0].Denominator));
        if (!withAudio)
        {
            // Without this the Finder preview and Logic's browser would announce an audio file the package has not.
            SetValue(root, "AudioFiles", new XElement("array"));
        }

        return SavePropertyList(document);
    }

    private byte[] BuildProjectInformation(string projectName)
    {
        var document = LoadPropertyList(template.Files[LogicTemplate.ProjectInformationPath]);
        var variants = FindValue(document.Root!.Element("dict")!, "VariantNames");
        if (variants is not null)
        {
            SetValue(variants, "0", new XElement("string", projectName));
        }

        return SavePropertyList(document);
    }

    private static XDocument LoadPropertyList(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return XDocument.Load(reader);
    }

    private static byte[] SavePropertyList(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "\t" }))
        {
            document.Save(writer);
        }

        return stream.ToArray();
    }

    private static XElement? FindValue(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static void SetValue(XElement dict, string key, XElement value)
    {
        var existing = FindValue(dict, key);
        if (existing is not null)
        {
            existing.ReplaceWith(value);
        }
        else
        {
            dict.Add(new XElement("key", key), value);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static long ToLogicTicks(long ticks, int ticksPerQuarterNote) =>
        (long)Math.Round(ticks * (double)LogicTicksPerQuarterNote / ticksPerQuarterNote, MidpointRounding.AwayFromZero);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static void ReplaceUInt32(byte[] buffer, uint oldValue, uint newValue)
    {
        Span<byte> pattern = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, oldValue);
        var start = 0;
        int index;
        while ((index = buffer.AsSpan(start).IndexOf(pattern)) >= 0)
        {
            WriteUInt32(buffer, start + index, newValue);
            start += index + 4;
        }
    }

    private static async Task WriteFileAsync(ILogicPackageSink sink, string path, byte[] content, CancellationToken cancellationToken)
    {
        await using var target = sink.CreateFile(path);
        await target.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
