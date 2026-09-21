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
    /// Writes a Logic Pro package with the score's tracks into <paramref name="sink"/>, and the audio the
    /// caller gives: the YuE recording and, if they were separated, its stems. An audio track the caller
    /// leaves out is removed from the project, which then opens without it. Nothing is written if the result
    /// reports an error.
    /// </summary>
    Task<LogicProjectResult> WriteAsync(
        ScoreDocument score,
        LogicAudio audio,
        ILogicPackageSink sink,
        LogicProjectOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Mix">The recording YuE wrote, as FLAC.</param>
/// <param name="Vocals">The separated vocals, as WAV.</param>
/// <param name="VocalsDry">The separated vocals without their reverb, as WAV.</param>
public sealed record LogicAudio(Stream? Mix, Stream? Vocals = null, Stream? VocalsDry = null)
{
    public IReadOnlyList<Stream?> Tracks => [Mix, Vocals, VocalsDry];
}

public sealed record LogicProjectOptions
{
    /// <summary>Name shown for the project alternative; the package file name is chosen by the host.</summary>
    public string ProjectName { get; set; } = "YuE";

    public int MelodyVelocity { get; set; } = 96;

    public int ChordVelocity { get; set; } = 72;

    /// <summary>
    /// Gives every track one region per song section, named after it, instead of a single region running the whole
    /// song. Sections can then be copied, looped or moved in Logic without cutting anything first. Without
    /// sections in the score this changes nothing.
    /// </summary>
    public bool SplitRegionsAtSections { get; set; }

    /// <summary>
    /// MIDI channel per track (1-16), keyed by track name; a track without an entry keeps the channel of the
    /// template's region. Logic sends a track's notes on this channel to an external instrument.
    /// </summary>
    public IReadOnlyDictionary<string, int> Channels { get; set; } = new Dictionary<string, int>();
}

public sealed record LogicProjectResult(bool Success, AudioStreamInfo? Audio, IReadOnlyList<Diagnostic> Diagnostics);

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
    private const byte MidiPlacement = 0x20;

    /// <summary>Offset of the track a placement sits on, inside the placement.</summary>
    private const int PlacementTrackOffset = 8;

    private const int GeneralMidiDrumChannel = 9;

    // Offsets inside the audio file (AuFl) and audio region (AuRg) payloads. What an audio file object holds
    // in front of the format is not of a fixed size, so its fields are counted from the format tag: "CaLf" is
    // "fLaC" backwards, the way this format stores four-character tags.
    private static readonly byte[] FlacFormatTag = "CaLf"u8.ToArray();
    private static readonly byte[] WaveFormatTag = "EVAW"u8.ToArray();

    /// <summary>Where macOS keeps the home folders, and with them everything that is true of one machine only.</summary>
    private static readonly byte[] UserFolder = "/Users/"u8.ToArray();

    /// <summary>Content Logic keeps itself and finds again without being told where it once was.</summary>
    private static readonly string[] LibraryContent = ["Logic Pro Library.bundle", ".logicx/"];

    /// <summary>Longer than any path a file system hands out, so a run of text this long is not one.</summary>
    private const int MaxPathLength = 1024;

    /// <summary>An audio file object opens with the name of its file: its length in characters, then UTF-16.</summary>
    private const int AudioFileNameLengthOffset = 8;
    private const int AudioFileNameOffset = 10;

    private const int AudioFileSamplesOffset = 12;
    private const int AudioFileSampleRateOffset = 20;
    private const int AudioFileChannelsOffset = 24;
    private const int AudioRegionSamplesOffset = 22;

    /// <summary>Name of the audio file an audio region plays: its length as a u16, then the UTF-8 name.</summary>
    private const int AudioRegionNameLengthOffset = 74;
    private const int AudioRegionNameOffset = 76;

    /// <summary>The audio region's id in the arrangement, which the usual region field of a placement leaves empty.</summary>
    private const int AudioPlacementRegionIdOffset = 44;

    private static readonly byte[] SequenceTerminator = Convert.FromHexString("F1000000FFFFFF3F0000000000000000");

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
        LogicAudio audio,
        ILogicPackageSink sink,
        LogicProjectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(sink);
        options ??= new LogicProjectOptions();
        var diagnostics = new DiagnosticBag();

        var prepared = await PrepareAudioAsync(audio.Tracks, diagnostics, cancellationToken).ConfigureAwait(false);
        var mix = prepared[0]?.Info;
        if (diagnostics.ToList().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new LogicProjectResult(false, mix, diagnostics.ToList());
        }

        if (mix is not null)
        {
            CheckAudio(score, mix, diagnostics);
        }

        var projectData = BuildProjectData(score, prepared, options, diagnostics);

        await WriteFileAsync(sink, LogicTemplate.ProjectDataPath, projectData, cancellationToken).ConfigureAwait(false);
        await WriteFileAsync(sink, LogicTemplate.MetaDataPath, BuildMetaData(score, prepared), cancellationToken).ConfigureAwait(false);
        await WriteFileAsync(sink, LogicTemplate.ProjectInformationPath, BuildProjectInformation(options.ProjectName), cancellationToken).ConfigureAwait(false);
        foreach (var (path, content) in template.Files)
        {
            if (path is not (LogicTemplate.ProjectDataPath or LogicTemplate.MetaDataPath or LogicTemplate.ProjectInformationPath))
            {
                await WriteFileAsync(sink, path, content, cancellationToken).ConfigureAwait(false);
            }
        }

        for (var slot = 0; slot < prepared.Length; slot++)
        {
            if (prepared[slot] is not { } file)
            {
                continue;
            }

            await using var target = sink.CreateFile($"Media/Audio Files/{AudioSlots[slot].FileName}");
            await target.WriteAsync(file.Header.AsMemory(0, file.HeaderLength), cancellationToken).ConfigureAwait(false);
            await file.Rest.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return new LogicProjectResult(true, mix, diagnostics.ToList());
    }

    // ---- Notes ------------------------------------------------------------------------------------

    private readonly record struct LogicNote(long Start, long Length, int Pitch, int Velocity);

    private static Dictionary<string, List<LogicNote>> CollectEvents(
        ScoreDocument score,
        IReadOnlyList<TemplateTrack> tracks,
        LogicProjectOptions options,
        DiagnosticBag diagnostics)
    {
        var events = tracks.ToDictionary(t => t.Region, _ => new List<LogicNote>(), StringComparer.Ordinal);
        long Ticks(long scoreTicks) => ToLogicTicks(scoreTicks, score.TicksPerQuarterNote);

        foreach (var voice in score.Voices)
        {
            // A track of the voice's own name comes first, so a split drum kit finds Kick, Snare and the rest;
            // only a voice the template has no track for falls back to the one its kind usually plays on.
            var region = events.ContainsKey(voice.Id)
                ? voice.Id
                : voice.Kind switch
                {
                    TrackKind.Chords => "Chords",
                    TrackKind.Bass => "Bass",
                    TrackKind.Drums => "Drums",
                    _ => voice.Id,
                };
            if (!events.TryGetValue(region, out var target) || (region is "Chords" && voice.Kind != TrackKind.Chords))
            {
                diagnostics.Warning(
                    DiagnosticCodes.LogicTemplateLimitation,
                    $"The Logic template has no track named '{region}', so voice '{voice.Id}' is only in the MIDI file. Its tracks are: {string.Join(", ", tracks.Select(t => t.Region))}. A template saved with a track of that name takes it as well.");
                continue;
            }

            target.AddRange(voice.Notes.Select(n => new LogicNote(
                Ticks(n.StartTicks), Ticks(n.DurationTicks), n.NoteNumber, n.Velocity ?? options.MelodyVelocity)));
        }

        // The arranger plays the chord symbols; only a score that has not been through it needs block chords here.
        if (events.TryGetValue("Chords", out var chordTrack) && chordTrack.Count == 0)
        {
            foreach (var chord in score.Chords.Where(c => c.Symbol is not null))
            {
                chordTrack.AddRange(ChordVoicing.GetNotes(chord.Symbol!).Select(pitch => new LogicNote(
                    Ticks(chord.StartTicks), Ticks(chord.DurationTicks), pitch, options.ChordVelocity)));
            }
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

    private byte[] BuildProjectData(
        ScoreDocument score,
        IReadOnlyList<PreparedAudio?> audio,
        LogicProjectOptions options,
        DiagnosticBag diagnostics)
    {
        var project = LogicProjectData.Parse(template.Files[LogicTemplate.ProjectDataPath]);
        var chunks = project.Chunks;
        var songTicks = checked((uint)ToLogicTicks(score.LengthTicks, score.TicksPerQuarterNote));

        // Which MIDI tracks exist comes from the template, so one saved with further tracks fills them too.
        var tracks = WithChannels(ReadTracks(chunks), options.Channels);
        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("The Logic template has no MIDI region in its arrangement.");
        }

        var events = CollectEvents(score, tracks, options, diagnostics);

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

        var audioTracks = ReadAudioTracks(chunks, audio);
        var created = new List<(uint Class, uint Id)>();
        if (options.SplitRegionsAtSections)
        {
            // Rebuilds the arrangement from scratch, so the single-region path below does not run at all.
            created.AddRange(WriteSectionRegions(chunks, score, tracks, events, audioTracks, songTicks));
        }
        else
        {
            foreach (var sequence in chunks.Where(c => c.Tag == "EvSq" && c.Class == ArrangementClass))
            {
                if (regionById.TryGetValue(sequence.Id, out var region))
                {
                    var channel = tracks.First(t => t.Region == region).Channel;
                    sequence.Payload = EncodeSequence(events[region], channel);
                }
                else if (sequence.Id == RootSequenceId)
                {
                    PlaceRegionsAtBarOne(sequence.Payload, AudioStart(score));
                }
            }
        }

        BlankUserPaths(chunks);
        SetTempo(chunks, score.TempoBpm);
        WriteSignatures(chunks, score);
        var removed = WriteAudioTracks(chunks, audioTracks, diagnostics);

        // Renamed before the regions are cut up, while the track list still matches the placements one to one.
        WriteTrackNames(chunks, tracks);

        created.AddRange(WriteChordTrack(chunks, score));
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

    /// <summary>
    /// The arrangement lists each region as five records; the first holds its start position. The MIDI regions
    /// begin at bar 1 and carry the count-in inside them; the audio, which has none, begins behind it.
    /// </summary>
    private static void PlaceRegionsAtBarOne(byte[] arrangement, uint audioStart)
    {
        for (var i = 0; i + 32 <= arrangement.Length; i += 16)
        {
            if (arrangement[i] is 0x20 or 0x24 && arrangement[i + 16 + 7] == 0x89)
            {
                WriteUInt32(arrangement, i + 4, arrangement[i] == AudioPlacement ? audioStart : ArrangementBar1);
            }
        }
    }

    /// <summary>Where the audio starts in the arrangement: bar 1, or behind the count-in if the score has one.</summary>
    private static uint AudioStart(ScoreDocument score) =>
        checked((uint)(ArrangementBar1 + ToLogicTicks(score.CountInTicks, score.TicksPerQuarterNote)));

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
    /// Clears the paths that say where Logic's own library content was found on the machine the template was
    /// built on - samples and impulse responses under its library bundle, or inside a project package. Logic
    /// finds that content by itself, and the path is of no use anywhere else. Every other path is left alone:
    /// a plug-in pointing somewhere of its own may well need it.
    /// </summary>
    /// <remarks>
    /// The strings sit in the channel strips (<c>AuCU</c>) as text among the plug-ins' own data, so they are
    /// blanked in place and nothing moves. Cleared is the run of printable characters and no byte more, which
    /// keeps the plug-in data around it - a patch of its own would otherwise be destroyed along with the path.
    /// </remarks>
    private static void BlankUserPaths(List<LogicChunk> chunks)
    {
        foreach (var chunk in chunks.Where(c => c.Tag == "AuCU"))
        {
            var payload = chunk.Payload;
            for (var offset = 0; offset < payload.Length;)
            {
                var found = payload.AsSpan(offset).IndexOf(UserFolder);
                if (found < 0)
                {
                    break;
                }

                var start = offset + found;
                var end = start;
                while (end < payload.Length && end - start <= MaxPathLength && payload[end] is >= 0x20 and < 0x7F)
                {
                    end++;
                }

                var path = Encoding.UTF8.GetString(payload, start, end - start);
                if (LibraryContent.Any(part => path.Contains(part, StringComparison.Ordinal)))
                {
                    payload.AsSpan(start, end - start).Clear();
                }

                offset = start + UserFolder.Length;
            }
        }
    }

    /// <summary>
    /// Tempo is stored as BPM · 10000 in the tempo list and in several places of the Song chunk. Each list is
    /// reduced to its first event (two records), since the score has a single tempo. A template can hold more than
    /// one list, because Logic keeps a tempo set of its own for every alternative.
    /// </summary>
    private static void SetTempo(List<LogicChunk> chunks, double bpm)
    {
        var newTempo = (uint)Math.Round(bpm * 10000);
        var song = chunks.Single(c => c.Tag == "Song");
        foreach (var tempoList in chunks.Where(c => c.Tag == "EvSq" && c.Class == TempoListClass).ToList())
        {
            var oldTempo = BinaryPrimitives.ReadUInt32LittleEndian(tempoList.Payload.AsSpan(16, 4));
            tempoList.Payload = [.. tempoList.Payload.AsSpan(0, 32), .. SequenceTerminator];
            ReplaceUInt32(tempoList.Payload, oldTempo, newTempo);
            ReplaceUInt32(song.Payload, oldTempo, newTempo);
        }
    }

    /// <summary>
    /// A template may hold more audio files than the one on its audio track, for instance because a first take was
    /// replaced: Logic keeps the others in the project audio pool. Only the placed one can be filled with the
    /// uploaded audio, so the rest is dropped, as is the file of a project written without audio.
    /// </summary>
    /// <returns>The objects removed, which have to leave the registry as well.</returns>
    private static List<(uint Class, uint Id)> KeepPlacedAudio(List<LogicChunk> chunks)
    {
        var placement = Records(Chunk(chunks, "EvSq", ArrangementClass, RootSequenceId).Payload, PlacementLength)
            .FirstOrDefault(p => p[0] == AudioPlacement);
        var placed = placement is null
            ? chunks.Where(c => c.Tag == "AuRg").Select(c => c.Id).DefaultIfEmpty(0u).Min()
            : ReadUInt32(placement, AudioPlacementRegionIdOffset);

        var others = chunks.Where(c => c.Tag is "AuFl" or "AuRg" && c.Id != placed).ToList();
        var removed = others.Select(c => ((uint)c.Class, c.Id)).Distinct().ToList();
        chunks.RemoveAll(others.Contains);
        return removed;
    }

    private static void SetAudio(List<LogicChunk> chunks, FlacStreamInfo audio)
    {
        var fileName = Path.GetFileName(LogicTemplate.AudioPath);
        var audioFile = chunks.First(c => c.Tag == "AuFl");
        audioFile.Payload = WithName(audioFile.Payload, AudioFileNameLengthOffset, AudioFileNameOffset, Encoding.Unicode, fileName);
        var file = audioFile.Payload;
        var format = file.AsSpan().LastIndexOf(FlacFormatTag);
        if (format < 0)
        {
            throw new InvalidOperationException("The Logic template's audio file is not a FLAC file.");
        }

        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(format + AudioFileSamplesOffset), (ulong)audio.TotalSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(format + AudioFileSampleRateOffset), (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(format + AudioFileChannelsOffset), (ushort)audio.Channels);

        // Forget the folder the template's audio came from, so Logic looks inside the package.
        var folder = file.AsSpan().IndexOf("PMOC"u8);
        if (folder >= 0)
        {
            var start = folder + 4;
            var end = file.AsSpan(start).IndexOf((byte)0);
            file.AsSpan(start, end < 0 ? file.Length - start : end).Clear();
        }

        var region = chunks.First(c => c.Tag == "AuRg");
        BinaryPrimitives.WriteUInt64LittleEndian(region.Payload.AsSpan(AudioRegionSamplesOffset), (ulong)audio.TotalSamples);
        region.Payload = WithName(region.Payload, AudioRegionNameLengthOffset, AudioRegionNameOffset, Encoding.UTF8, fileName);
    }

    /// <summary>
    /// Writes the name of the audio file, which the file object carries in UTF-16 and its region in UTF-8. The
    /// package always writes the audio under the same name, while a template can carry a name of its own: Logic
    /// numbers a second import "audio_1.flac", and looks for exactly that name when the project is opened.
    /// </summary>
    private static byte[] WithName(byte[] payload, int lengthOffset, int nameOffset, Encoding encoding, string fileName)
    {
        var characters = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(lengthOffset));
        var name = encoding.GetBytes(fileName);
        var tail = nameOffset + (characters * (encoding is UnicodeEncoding ? 2 : 1));
        if (tail > payload.Length)
        {
            return payload;
        }

        byte[] patched = [.. payload.AsSpan(0, nameOffset), .. name, .. payload.AsSpan(tail)];
        BinaryPrimitives.WriteUInt16LittleEndian(patched.AsSpan(lengthOffset), (ushort)fileName.Length);
        return patched;
    }

    private static void CheckAudio(ScoreDocument score, AudioStreamInfo audio, DiagnosticBag diagnostics)
    {
        if (audio.Channels != 2)
        {
            diagnostics.Warning(DiagnosticCodes.InvalidAudio, Invariant($"The audio has {audio.Channels} channel(s); the template's audio track is stereo."));
        }

        // Only the music is compared: a count-in is silence the recording never had.
        var music = score.MusicDurationSeconds;
        var difference = Math.Abs(audio.DurationSeconds - music);
        if (difference > Math.Max(5, music * 0.05))
        {
            diagnostics.Warning(
                DiagnosticCodes.AudioLengthMismatch,
                Invariant($"The audio lasts {audio.DurationSeconds:0.0} s but the score {music:0.0} s; is it the audio.flac from the same YuE run? '--fit-tempo' adjusts the tempo when the difference is a drift."));
        }
    }

    // ---- Property lists ---------------------------------------------------------------------------

    private byte[] BuildMetaData(ScoreDocument score, IReadOnlyList<PreparedAudio?> audio)
    {
        var document = LoadPropertyList(template.Files[LogicTemplate.MetaDataPath]);
        var root = document.Root!.Element("dict")!;
        SetValue(root, "BeatsPerMinute", new XElement("real", score.TempoBpm.ToString("0.####", CultureInfo.InvariantCulture)));
        SetValue(root, "SongSignatureNumerator", new XElement("integer", score.TimeSignatures[0].Numerator));
        SetValue(root, "SongSignatureDenominator", new XElement("integer", score.TimeSignatures[0].Denominator));
        // The package holds exactly the audio it was given, whatever the template listed here; without this the
        // Finder preview and Logic's browser would announce files the package has not.
        SetValue(root, "UnusedAudioFiles", new XElement("array"));
        SetValue(
            root,
            "AudioFiles",
            new XElement(
                "array",
                audio.Index()
                    .Where(entry => entry.Item is not null)
                    .Select(entry => new XElement("string", $"Audio Files/{AudioSlots[entry.Index].FileName}"))));

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
