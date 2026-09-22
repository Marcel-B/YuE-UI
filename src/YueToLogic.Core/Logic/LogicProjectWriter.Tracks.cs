using System.Buffers.Binary;
using System.Text;

namespace YueToLogic.Core.Logic;

/// <summary>
/// The MIDI tracks a template offers, and their names in Logic's arrangement.
/// </summary>
/// <remarks>
/// Which tracks exist is read from the template rather than fixed in code, so a template saved with more
/// tracks - a guide-tone or a doubling track, say - fills them without this having to change. A track is
/// recognized by the region sitting on it at bar 1, which is what the generated MIDI file names it after.
///
/// The name Logic shows in the track header belongs to the channel strip in its environment, which is why a
/// track that had an instrument chosen for it reads "Studio Grand" rather than "Vocal". The arrangement's
/// track list points at those channel strips, so the writer renames them after the parts they carry.
/// </remarks>
public sealed partial class LogicProjectWriter
{
    /// <summary>Environment objects, which hold the channel strips and their names.</summary>
    private const ushort EnvironmentClass = 20;

    /// <summary>Offset of the name length inside an environment object; the UTF-8 name follows it.</summary>
    private const int EnvironmentNameOffset = 158;

    /// <summary>
    /// A region names the channel strip of the track it lies on, at a fixed distance behind its own name.
    /// </summary>
    private const int RegionEnvironmentOffset = 204;

    /// <summary>Between a track's part and its instrument in the name Logic shows: "Bass · Mother32".</summary>
    private const string InstrumentSeparator = " · ";

    /// <summary>
    /// A MIDI track of the template: the region on it, its channel strip, the channel it plays on and, if the
    /// caller named one, the instrument it plays and the MIDI output that instrument listens on.
    /// </summary>
    private readonly record struct TemplateTrack(
        string Region,
        uint RegionId,
        uint Environment,
        int Channel,
        string? Instrument = null,
        string? Port = null)
    {
        /// <summary>The name for the track header: the part alone, or the part and its instrument.</summary>
        public string Title => Instrument is null ? Region : Region + InstrumentSeparator + Instrument;
    }

    /// <summary>
    /// The template's MIDI tracks, in the order the arrangement places them. Only regions placed in the root
    /// sequence count, which leaves out the chord track's own regions.
    /// </summary>
    private static List<TemplateTrack> ReadTracks(List<LogicChunk> chunks)
    {
        var names = chunks
            .Where(c => c.Tag == "MSeq" && c.Class == ArrangementClass)
            .ToDictionary(c => c.Id, c => c.SequenceName);
        var sequences = chunks
            .Where(c => c.Tag == "EvSq" && c.Class == ArrangementClass)
            .ToDictionary(c => c.Id, c => c.Payload);

        var root = Chunk(chunks, "EvSq", ArrangementClass, RootSequenceId);
        var tracks = new List<TemplateTrack>();
        var used = new HashSet<int>();

        foreach (var placement in Records(root.Payload, PlacementLength))
        {
            if (placement[0] != MidiPlacement)
            {
                continue;
            }

            var id = ReadUInt32(placement, PlacementRegionIdOffset);
            if (!names.TryGetValue(id, out var name))
            {
                continue;
            }

            var channel = ChannelOf(sequences.GetValueOrDefault(id), name, used);
            used.Add(channel);
            tracks.Add(new TemplateTrack(name, id, EnvironmentOf(Chunk(chunks, "MSeq", ArrangementClass, id)), channel));
        }

        return tracks;
    }

    /// <summary>
    /// The channel Logic stored the region's notes on. An empty region has none to read, so a track named
    /// after drums falls back to the General MIDI drum channel and everything else to the next free one.
    /// </summary>
    private static int ChannelOf(byte[]? sequence, string name, HashSet<int> used)
    {
        if (sequence is not null)
        {
            for (var offset = 0; offset + 16 <= sequence.Length; offset += 16)
            {
                if ((sequence[offset] & 0xF0) == 0x90)
                {
                    return sequence[offset] & 0x0F;
                }
            }
        }

        if (name.Contains("drum", StringComparison.OrdinalIgnoreCase))
        {
            return GeneralMidiDrumChannel;
        }

        for (var channel = 0; channel < 16; channel++)
        {
            if (channel != GeneralMidiDrumChannel && !used.Contains(channel))
            {
                return channel;
            }
        }

        return 0;
    }

    /// <summary>
    /// Puts the instruments and channels the caller chose on the tracks they name; the rest keep the channel of
    /// the template's region. An instrument decides the channel ahead of a bare channel entry, and names the
    /// track. Channels are counted from one outside, as Logic and every MIDI device do.
    /// </summary>
    private static List<TemplateTrack> WithRouting(
        List<TemplateTrack> tracks,
        IReadOnlyDictionary<string, int> channels,
        IReadOnlyDictionary<string, LogicInstrument> instruments)
    {
        if (channels.Count == 0 && instruments.Count == 0)
        {
            return tracks;
        }

        var givenChannels = new Dictionary<string, int>(channels, StringComparer.OrdinalIgnoreCase);
        var givenInstruments = new Dictionary<string, LogicInstrument>(instruments, StringComparer.OrdinalIgnoreCase);
        return [.. tracks.Select(track =>
        {
            if (givenInstruments.TryGetValue(track.Region, out var instrument) && !string.IsNullOrWhiteSpace(instrument.Name))
            {
                return track with
                {
                    Instrument = instrument.Name.Trim(),
                    Port = string.IsNullOrWhiteSpace(instrument.Port) ? null : instrument.Port.Trim(),
                    Channel = instrument.Channel is >= 1 and <= 16 ? instrument.Channel - 1 : track.Channel,
                };
            }

            return givenChannels.TryGetValue(track.Region, out var channel) && channel is >= 1 and <= 16
                ? track with { Channel = channel - 1 }
                : track;
        })];
    }

    /// <summary>
    /// The channel strip a region lies on, taken from the region itself: the id sits behind the region's name,
    /// which is why the offset follows the name's padded length, as a sequence's own length field does.
    /// </summary>
    private static uint EnvironmentOf(LogicChunk region) =>
        region.SequenceLengthOffset - 60 + RegionEnvironmentOffset + 4 <= region.Payload.Length
            ? ReadUInt32(region.Payload, region.SequenceLengthOffset - 60 + RegionEnvironmentOffset)
            : 0;

    /// <summary>
    /// Names each track after the part it carries, so the arrangement reads "Vocal" instead of the instrument
    /// that happened to be chosen for it in the template - or "Bass · Mother32" where the caller said which
    /// hardware plays it. Every MIDI region names its own channel strip, so the audio track and the output keep
    /// theirs, however the tracks are ordered.
    /// </summary>
    private static void WriteTrackNames(List<LogicChunk> chunks, IReadOnlyList<TemplateTrack> tracks)
    {
        var environment = chunks
            .Where(c => c.Tag == "Envi" && c.Class == EnvironmentClass && c.Payload.Length > EnvironmentNameOffset + 2)
            .ToDictionary(c => c.Id);

        foreach (var track in tracks)
        {
            if (environment.TryGetValue(track.Environment, out var strip))
            {
                RenameEnvironment(strip, track.Title);
            }
        }
    }

    /// <summary>The name a channel strip carries, which is what Logic shows in the track header.</summary>
    private static string EnvironmentName(LogicChunk strip)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(strip.Payload.AsSpan(EnvironmentNameOffset, 2));
        return length == 0 || EnvironmentNameOffset + 2 + length > strip.Payload.Length
            ? string.Empty
            : Encoding.UTF8.GetString(strip.Payload, EnvironmentNameOffset + 2, length);
    }

    /// <summary>
    /// Renames a channel strip. Its name sits at a fixed offset as a length and its UTF-8 bytes, padded to an
    /// even size, exactly as a sequence name does; everything behind it simply moves along.
    /// </summary>
    private static void RenameEnvironment(LogicChunk strip, string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        var oldLength = BinaryPrimitives.ReadUInt16LittleEndian(strip.Payload.AsSpan(EnvironmentNameOffset, 2));
        var restOffset = EnvironmentNameOffset + 2 + oldLength + (oldLength & 1);
        if (restOffset > strip.Payload.Length)
        {
            return;
        }

        var rest = strip.Payload.AsSpan(restOffset).ToArray();
        var bytes = Encoding.UTF8.GetBytes(name);
        var padding = bytes.Length & 1;

        var payload = new byte[EnvironmentNameOffset + 2 + bytes.Length + padding + rest.Length];
        strip.Payload.AsSpan(0, EnvironmentNameOffset).CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(EnvironmentNameOffset, 2), (ushort)bytes.Length);
        bytes.CopyTo(payload.AsSpan(EnvironmentNameOffset + 2));
        rest.CopyTo(payload.AsSpan(EnvironmentNameOffset + 2 + bytes.Length + padding));
        strip.Payload = payload;
    }
}
