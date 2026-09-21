using System.Buffers.Binary;
using System.Text;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Logic;

/// <summary>
/// Splitting the MIDI tracks into one region per song section. Instead of a single region per track running the
/// whole song, every track gets a region named after the section it covers ("Verse 1", "Chorus 2", …), which is
/// what makes an arrangement workable in Logic: a section can be copied, looped, muted or moved on its own.
/// </summary>
/// <remarks>
/// A region is three chunks with a shared id - the sequence (<c>MSeq</c>, which carries name and length), its
/// track entry (<c>Trak</c>) and its events (<c>EvSq</c>) - plus an 80-byte placement in the root sequence that
/// says where on which track it sits. Regions beyond the template's are cloned from it, exactly as
/// <see cref="WriteChordTrack"/> does for the chord track, and registered as new objects.
/// </remarks>
public sealed partial class LogicProjectWriter
{
    /// <summary>Offset of the region id inside an 80-byte placement; the audio placement has no region.</summary>
    private const int PlacementRegionIdOffset = 32;

    private const uint NoRegion = 0xFFFFFFFF;

    /// <summary>A stretch of the song one region covers, named after the section that starts it.</summary>
    private readonly record struct RegionSegment(string Name, long StartTicks, long EndTicks);

    /// <summary>
    /// Replaces the five full-length regions with one region per section and track, and rebuilds the arrangement
    /// accordingly. Tracks without a single note keep one empty region, as they have without splitting.
    /// </summary>
    /// <returns>The objects created for the extra regions, which have to enter the registry.</returns>
    private static List<(uint Class, uint Id)> WriteSectionRegions(
        List<LogicChunk> chunks,
        ScoreDocument score,
        Dictionary<string, List<LogicNote>> events,
        bool withAudio,
        uint songTicks)
    {
        var segments = Segments(score);
        var root = Chunk(chunks, "EvSq", ArrangementClass, RootSequenceId);
        var placements = Records(root.Payload, PlacementLength).ToList();

        // Every MIDI placement names the region it holds; the audio placement is kept as it is.
        var placementByRegion = placements
            .Where(p => ReadUInt32(p, PlacementRegionIdOffset) != NoRegion)
            .ToDictionary(p => ReadUInt32(p, PlacementRegionIdOffset));
        var audioPlacement = placements.Find(p => p[0] == AudioPlacement);

        var regionByName = chunks
            .Where(c => c.Tag == "MSeq" && c.Class == ArrangementClass && events.ContainsKey(c.SequenceName))
            .ToDictionary(c => c.SequenceName, c => c.Id, StringComparer.Ordinal);

        var usedIds = chunks.Where(c => c.Tag is "MSeq" or "Trak" or "EvSq").Select(c => c.Id).ToHashSet();
        var nextId = chunks.Where(c => c.Tag == "MSeq" && c.Class == ArrangementClass).Max(c => c.Id) + 4;
        var created = new List<(uint Class, uint Id)>();
        var clones = new List<LogicChunk>();

        using var arrangement = new MemoryStream();
        if (withAudio && audioPlacement is not null)
        {
            var placement = audioPlacement.ToArray();
            WriteUInt32(placement, 4, ArrangementBar1);
            arrangement.Write(placement);
        }

        foreach (var (name, channel) in Regions)
        {
            var sourceId = regionByName[name];
            var notes = events[name];
            var slices = new List<(RegionSegment Segment, List<LogicNote> Notes)>();
            foreach (var segment in segments)
            {
                var sliceNotes = notes.FindAll(n => n.Start >= segment.StartTicks && n.Start < segment.EndTicks);
                if (sliceNotes.Count > 0)
                {
                    slices.Add((segment, sliceNotes));
                }
            }

            // A track the score has nothing for keeps the one empty region it has without splitting.
            if (slices.Count == 0)
            {
                slices.Add((new RegionSegment(name, 0, songTicks), []));
            }

            for (var i = 0; i < slices.Count; i++)
            {
                var (segment, sliceNotes) = slices[i];
                var region = Chunk(chunks, "MSeq", ArrangementClass, sourceId);
                var sequence = Chunk(chunks, "EvSq", ArrangementClass, sourceId);
                var id = sourceId;

                if (i > 0)
                {
                    while (usedIds.Contains(nextId))
                    {
                        nextId += 4;
                    }

                    id = nextId;
                    nextId += 4;
                    usedIds.Add(id);
                    created.Add((ArrangementClass, id));
                    region = Clone(region, id);
                    sequence = Clone(sequence, id);
                    clones.AddRange([region, Clone(Chunk(chunks, "Trak", ArrangementClass, sourceId), id), sequence]);
                }

                // A note that reaches past its section keeps its length; the region grows with it instead, so
                // nothing is muted at a section border.
                var end = sliceNotes.Count == 0
                    ? segment.EndTicks
                    : Math.Max(segment.EndTicks, sliceNotes.Max(n => n.Start + Math.Max(1, n.Length)));

                Rename(region, segment.Name);
                WriteUInt32(region.Payload, region.SequenceLengthOffset, checked((uint)Math.Max(1, end - segment.StartTicks)));
                sequence.Payload = EncodeSequence(
                    [.. sliceNotes.Select(n => n with { Start = n.Start - segment.StartTicks })],
                    channel);

                var placement = placementByRegion[sourceId].ToArray();
                WriteUInt32(placement, 4, checked((uint)(ArrangementBar1 + segment.StartTicks)));
                WriteUInt32(placement, PlacementRegionIdOffset, id);
                arrangement.Write(placement);
            }
        }

        arrangement.Write(SequenceTerminator);
        root.Payload = arrangement.ToArray();

        var lastRegionChunk = chunks.FindLastIndex(c =>
            c.Class == ArrangementClass && c.Tag is "MSeq" or "Trak" or "EvSq" && regionByName.ContainsValue(c.Id));
        chunks.InsertRange(lastRegionChunk + 1, clones);
        return created;
    }

    /// <summary>
    /// The song cut at its section starts. A stretch before the first section keeps the track's own name; a
    /// section name that occurs more than once is numbered, so "Verse 1" and "Verse 2" can be told apart.
    /// </summary>
    private static List<RegionSegment> Segments(ScoreDocument score)
    {
        var length = ToLogicTicks(score.LengthTicks, score.TicksPerQuarterNote);
        var starts = score.Sections
            .Select(section => (Name: DisplayName(section.Name), Start: ToLogicTicks(section.StartTicks, score.TicksPerQuarterNote)))
            .Where(section => section.Start < length)
            .OrderBy(section => section.Start)
            .ToList();

        if (starts.Count == 0)
        {
            return [new RegionSegment(string.Empty, 0, length)];
        }

        var counts = starts.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var segments = new List<RegionSegment>();
        if (starts[0].Start > 0)
        {
            segments.Add(new RegionSegment(string.Empty, 0, starts[0].Start));
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var (name, start) = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1].Start : Math.Max(length, start + 1);
            seen[name] = seen.GetValueOrDefault(name) + 1;
            var label = counts[name] > 1 ? $"{name} {seen[name]}" : name;
            segments.Add(new RegionSegment(label, start, end));
        }

        return segments;
    }

    /// <summary>
    /// Renames a sequence. The name sits at the front of the payload as a length and its Latin-1 bytes, padded to
    /// an even size; everything behind it is addressed relative to its end and therefore simply moves along.
    /// </summary>
    private static void Rename(LogicChunk sequence, string name)
    {
        if (name.Length == 0 || sequence.SequenceName == name)
        {
            return;
        }

        var oldLength = BinaryPrimitives.ReadUInt16LittleEndian(sequence.Payload.AsSpan(16, 2));
        var rest = sequence.Payload.AsSpan(18 + oldLength + (oldLength & 1)).ToArray();
        var bytes = Encoding.Latin1.GetBytes(name);
        var padding = bytes.Length & 1;

        var payload = new byte[18 + bytes.Length + padding + rest.Length];
        sequence.Payload.AsSpan(0, 16).CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), (ushort)bytes.Length);
        bytes.CopyTo(payload.AsSpan(18));
        rest.CopyTo(payload.AsSpan(18 + bytes.Length + padding));
        sequence.Payload = payload;
    }
}
