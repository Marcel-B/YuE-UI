using System.Buffers.Binary;
using System.Text;
using YueToLogic.Core.Diagnostics;

namespace YueToLogic.Core.Logic;

/// <summary>The audio tracks of a project: the YuE recording and, if they were separated, its stems.</summary>
/// <remarks>
/// A template's audio tracks are taken in the order its arrangement places them: the recording first, then the
/// vocals and the dry vocals from StemMyWav. Which of them a project gets is up to the caller; a track without
/// a file leaves the project altogether, as it would have nothing to play.
/// </remarks>
public sealed partial class LogicProjectWriter
{
    /// <summary>What the audio tracks hold, in the order the template places them.</summary>
    private static readonly (string FileName, string TrackName)[] AudioSlots =
    [
        ("audio.flac", "Mix"),
        ("vocals.wav", "Vocals"),
        ("vocals_dry.wav", "Vocals dry"),
    ];

    /// <summary>An audio file of a project: its objects in the template and the file that fills them.</summary>
    private sealed record AudioTrack(int Slot, LogicChunk File, LogicChunk Region, PreparedAudio? Audio)
    {
        public string FileName => AudioSlots[Slot].FileName;

        public string Path => $"Media/Audio Files/{FileName}";
    }

    /// <summary>A file the caller handed over, read far enough to know its format and length.</summary>
    private sealed record PreparedAudio(AudioStreamInfo Info, byte[] Header, int HeaderLength, Stream Rest);

    /// <summary>
    /// Reads the header of every file the caller gave, so that a recording that is not audio at all is refused
    /// before anything is written.
    /// </summary>
    private static async Task<PreparedAudio?[]> PrepareAudioAsync(
        IReadOnlyList<Stream?> audio,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        var prepared = new PreparedAudio?[AudioSlots.Length];
        for (var slot = 0; slot < prepared.Length && slot < audio.Count; slot++)
        {
            if (audio[slot] is not { } stream)
            {
                continue;
            }

            var header = new byte[AudioStreamInfo.HeaderLength];
            var length = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (!AudioStreamInfo.TryParse(header.AsSpan(0, length), out var info))
            {
                diagnostics.Error(
                    DiagnosticCodes.InvalidAudio,
                    $"The file for the {AudioSlots[slot].TrackName.ToLowerInvariant()} track is neither a FLAC nor a WAV file.");
                return prepared;
            }

            if (info!.SampleRate != RequiredSampleRate)
            {
                diagnostics.Error(
                    DiagnosticCodes.UnsupportedSampleRate,
                    Invariant($"The {AudioSlots[slot].TrackName.ToLowerInvariant()} has {info.SampleRate} Hz; the Logic project needs {RequiredSampleRate} Hz (YuE writes 48 kHz)."));
                return prepared;
            }

            prepared[slot] = new PreparedAudio(info, header, length, stream);
        }

        return prepared;
    }

    /// <summary>
    /// Fills the audio tracks the caller gave a file for and removes the rest, so that Logic never looks for a
    /// file the package has not. The template's own audio files are replaced whatever they were called.
    /// </summary>
    /// <returns>The objects that left the project and have to leave the registry as well.</returns>
    private static List<(uint Class, uint Id)> WriteAudioTracks(
        List<LogicChunk> chunks,
        IReadOnlyList<AudioTrack> tracks,
        DiagnosticBag diagnostics)
    {
        var arrangement = Chunk(chunks, "EvSq", ArrangementClass, RootSequenceId);
        // Read before the regions are renamed: the strip to rename carries the name of the template's own file.
        var strips = tracks.ToDictionary(track => track.Slot, TemplateStripName);
        var keep = new List<uint>();
        foreach (var track in tracks)
        {
            if (track.Audio is not { } audio)
            {
                continue;
            }

            var format = FormatOf(track.File.Payload);
            if (format != audio.Info.Format)
            {
                diagnostics.Warning(
                    DiagnosticCodes.LogicTemplateLimitation,
                    $"The template's {AudioSlots[track.Slot].TrackName.ToLowerInvariant()} track holds a {format} file, the one given is {audio.Info.Format}; Logic may not play it.");
            }

            SetAudioFile(track.File.Payload, audio.Info);
            track.File.Payload = WithName(track.File.Payload, AudioFileNameLengthOffset, AudioFileNameOffset, Encoding.Unicode, track.FileName);
            BinaryPrimitives.WriteUInt64LittleEndian(track.Region.Payload.AsSpan(AudioRegionSamplesOffset), (ulong)audio.Info.TotalSamples);
            track.Region.Payload = WithName(track.Region.Payload, AudioRegionNameLengthOffset, AudioRegionNameOffset, Encoding.UTF8, track.FileName);
            keep.Add(track.Region.Id);
        }

        NameAudioTracks(chunks, tracks, strips);
        arrangement.Payload = WithAudioPlacements(arrangement.Payload, keep);
        var gone = chunks.Where(c => c.Tag is "AuFl" or "AuRg" && !keep.Contains(c.Id)).ToList();
        chunks.RemoveAll(gone.Contains);
        return [.. gone.Select(c => ((uint)c.Class, c.Id)).Distinct()];
    }

    /// <summary>
    /// Names each audio track after what it now plays. Logic names an audio track after the file that was
    /// dropped on it, so the strip to rename is the one carrying the name of the template's own file.
    /// </summary>
    private static void NameAudioTracks(List<LogicChunk> chunks, IReadOnlyList<AudioTrack> tracks, IReadOnlyDictionary<int, string> names)
    {
        var strips = chunks
            .Where(c => c.Tag == "Envi" && c.Class == EnvironmentClass && c.Payload.Length > EnvironmentNameOffset + 2)
            .ToList();

        // Named whether or not they were given a file: an empty track then says what belongs on it.
        foreach (var track in tracks)
        {
            var strip = strips.Find(c => EnvironmentName(c) == names[track.Slot]);
            if (strip is not null)
            {
                RenameEnvironment(strip, AudioSlots[track.Slot].TrackName);
            }
        }
    }

    /// <summary>The name Logic gave the track when the template's file was dropped on it: the file without its extension.</summary>
    private static string TemplateStripName(AudioTrack track)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(track.Region.Payload.AsSpan(AudioRegionNameLengthOffset));
        var fileName = Encoding.UTF8.GetString(track.Region.Payload, AudioRegionNameOffset, length);
        return System.IO.Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>The audio tracks of the template, in the order its arrangement places them.</summary>
    private static List<AudioTrack> ReadAudioTracks(List<LogicChunk> chunks, IReadOnlyList<PreparedAudio?> audio)
    {
        var files = chunks.Where(c => c.Tag == "AuFl").ToDictionary(c => c.Id);
        var regions = chunks.Where(c => c.Tag == "AuRg").ToDictionary(c => c.Id);
        var tracks = new List<AudioTrack>();

        foreach (var placement in Records(Chunk(chunks, "EvSq", ArrangementClass, RootSequenceId).Payload, PlacementLength))
        {
            var id = ReadUInt32(placement, AudioPlacementRegionIdOffset);
            if (placement[0] != AudioPlacement || !regions.TryGetValue(id, out var region) || !files.TryGetValue(id, out var file))
            {
                continue;
            }

            var slot = tracks.Count;
            if (slot >= AudioSlots.Length)
            {
                break;
            }

            tracks.Add(new AudioTrack(slot, file, region, slot < audio.Count ? audio[slot] : null));
        }

        return tracks;
    }

    /// <summary>The arrangement with only the audio regions that are kept, the MIDI placements untouched.</summary>
    private static byte[] WithAudioPlacements(byte[] arrangement, IReadOnlyCollection<uint> keep)
    {
        using var kept = new MemoryStream();
        for (var offset = 0; offset + PlacementLength <= arrangement.Length; offset += PlacementLength)
        {
            if (arrangement[offset] != AudioPlacement || keep.Contains(ReadUInt32(arrangement, offset + AudioPlacementRegionIdOffset)))
            {
                kept.Write(arrangement, offset, PlacementLength);
            }
        }

        // What is left over at the end is the terminator that closes every list.
        var rest = arrangement.Length % PlacementLength;
        kept.Write(arrangement, arrangement.Length - rest, rest);
        return kept.ToArray();
    }

    /// <summary>Length, rate and channel count sit behind the format tag of the audio file object.</summary>
    private static void SetAudioFile(byte[] file, AudioStreamInfo audio)
    {
        var format = FormatOffset(file);
        if (format < 0)
        {
            return;
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
    }

    /// <summary>Four-character tags are stored backwards, so FLAC reads "CaLf" and WAVE "EVAW".</summary>
    private static int FormatOffset(byte[] file)
    {
        var flac = file.AsSpan().LastIndexOf(FlacFormatTag);
        return flac >= 0 ? flac : file.AsSpan().LastIndexOf(WaveFormatTag);
    }

    private static AudioFormat FormatOf(byte[] file) =>
        file.AsSpan().LastIndexOf(FlacFormatTag) >= 0 ? AudioFormat.Flac : AudioFormat.Wave;
}
