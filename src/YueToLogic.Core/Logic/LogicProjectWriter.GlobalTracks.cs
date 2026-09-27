using System.Buffers.Binary;
using System.Text;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Logic;

/// <summary>The chord track and the arrangement markers (Logic's global tracks).</summary>
public sealed partial class LogicProjectWriter
{
    private const ushort MarkerListClass = 5;
    private const ushort TextClass = 32;
    private const string ChordTrackName = "Global Harmonies";

    /// <summary>Offset of a chord region's own id inside its <c>MSeq</c> payload.</summary>
    private const int ChordRegionIdOffset = 202;

    /// <summary>A placement on the chord track: five 16-byte records (start, flags, region id, …).</summary>
    private const int ChordPlacementLength = 80;

    /// <summary>An arrangement marker: three 16-byte records (start, text id / colour / length, padding).</summary>
    private const int MarkerLength = 48;

    /// <summary>Size of the header in front of a marker name in a text chunk (<c>TxSq</c>).</summary>
    private const int TextHeaderLength = 98;

    /// <summary>
    /// Writes one chord region per chord, as Logic does when chords are entered on the chord track. The template's
    /// chord regions are reused; further regions are cloned from the first one and returned for registration.
    /// </summary>
    private static List<(uint Class, uint Id)> WriteChordTrack(List<LogicChunk> chunks, ScoreDocument score)
    {
        var track = chunks.Single(c => c.Tag == "MSeq" && c.Class == ArrangementClass && c.SequenceName == ChordTrackName);
        var placements = chunks.Single(c => c.Tag == "EvSq" && c.Class == ArrangementClass && c.Id == track.Id);
        var templateIds = Records(placements.Payload, ChordPlacementLength).Select(p => ReadUInt32(p, 32)).ToList();
        if (templateIds.Count == 0)
        {
            throw new InvalidOperationException("The Logic template's chord track has no chord to copy.");
        }

        var placementTemplate = placements.Payload.AsSpan(0, ChordPlacementLength).ToArray();
        var first = templateIds[0];
        var regionTemplate = Chunk(chunks, "MSeq", ArrangementClass, first);
        var trackTemplate = Chunk(chunks, "Trak", ArrangementClass, first);
        var eventTemplate = Chunk(chunks, "EvSq", ArrangementClass, first).Payload;
        if (ReadUInt32(regionTemplate.Payload, ChordRegionIdOffset) != first)
        {
            throw new InvalidOperationException("The Logic template's chord region has an unexpected layout.");
        }

        var chords = score.Chords.Where(c => c.Symbol is not null && c.DurationTicks > 0).ToList();
        var ids = templateIds.Take(chords.Count).ToList();
        var created = new List<(uint Class, uint Id)>();
        var clones = new List<LogicChunk>();
        // Track objects carry the id of the sequence they belong to, whatever its class, so a new region id
        // must not be used by any sequence, track or event list.
        var usedIds = chunks.Where(c => c.Tag is "MSeq" or "Trak" or "EvSq").Select(c => c.Id).ToHashSet();
        var nextId = chunks.Where(c => c.Tag == "MSeq" && c.Class == ArrangementClass).Max(c => c.Id) + 4;
        while (ids.Count < chords.Count)
        {
            while (usedIds.Contains(nextId))
            {
                nextId += 4;
            }

            var id = nextId;
            nextId += 4;
            ids.Add(id);
            created.Add((ArrangementClass, id));
            var region = Clone(regionTemplate, id);
            WriteUInt32(region.Payload, ChordRegionIdOffset, id);
            clones.AddRange([region, Clone(trackTemplate, id), Clone(Chunk(chunks, "EvSq", ArrangementClass, first), id)]);
        }

        var lastTemplateChunk = chunks.FindLastIndex(c => c.Class == ArrangementClass && templateIds.Contains(c.Id) && c.Tag is "MSeq" or "Trak" or "EvSq");
        chunks.InsertRange(lastTemplateChunk + 1, clones);

        using var placementList = new MemoryStream();
        for (var i = 0; i < chords.Count; i++)
        {
            var chord = chords[i];
            var region = Chunk(chunks, "MSeq", ArrangementClass, ids[i]);
            WriteUInt32(region.Payload, region.SequenceLengthOffset, (uint)ToLogicTicks(chord.DurationTicks, score.TicksPerQuarterNote));

            // Key record, chord head (at the region start) and chord data, as in the template region.
            var events = eventTemplate.AsSpan(0, 64).ToArray();
            WriteChordRegionKey(events, score, chord.StartTicks);
            WriteUInt32(events, 32 + 4, EventOrigin);
            LogicChordEncoding.Encode(events.AsSpan(48, LogicChordEncoding.RecordLength), chord.Text, chord.Symbol!);
            Chunk(chunks, "EvSq", ArrangementClass, ids[i]).Payload = [.. events, .. SequenceTerminator];

            var placement = placementTemplate.ToArray();
            WriteUInt32(placement, 4, checked((uint)(ArrangementBar1 + ToLogicTicks(chord.StartTicks, score.TicksPerQuarterNote))));
            WriteUInt32(placement, 32, ids[i]);
            placementList.Write(placement);
        }

        placementList.Write(SequenceTerminator);
        placements.Payload = placementList.ToArray();
        return created;
    }

    /// <summary>One arrangement marker per section, named after it and spanning it; names live in text chunks.</summary>
    private static List<(uint Class, uint Id)> WriteArrangementMarkers(List<LogicChunk> chunks, ScoreDocument score)
    {
        var list = chunks.Single(c => c.Tag == "EvSq" && c.Class == MarkerListClass && c.Payload.Length > MarkerLength && c.Payload[0] == 0x12);
        var templateTextIds = Records(list.Payload, MarkerLength).Select(m => ReadUInt32(m, 16)).ToList();
        if (templateTextIds.Count == 0)
        {
            throw new InvalidOperationException("The Logic template has no arrangement marker to copy.");
        }

        var markerTemplate = list.Payload.AsSpan(0, MarkerLength).ToArray();
        var textTemplate = Chunk(chunks, "TxSq", TextClass, templateTextIds[0]);
        var textHeader = textTemplate.Payload.AsSpan(0, TextHeaderLength).ToArray();

        var created = new List<(uint Class, uint Id)>();
        var nextId = chunks.Where(c => c.Tag == "TxSq" && c.Class == TextClass).Max(c => c.Id) + 4;
        var insertAt = chunks.FindLastIndex(c => c.Tag == "TxSq") + 1;
        using var markers = new MemoryStream();
        for (var i = 0; i < score.Sections.Count; i++)
        {
            var section = score.Sections[i];
            var end = i + 1 < score.Sections.Count ? score.Sections[i + 1].StartTicks : score.LengthTicks;
            uint textId;
            if (i < templateTextIds.Count)
            {
                textId = templateTextIds[i];
            }
            else
            {
                textId = nextId;
                nextId += 4;
                created.Add((TextClass, textId));
                chunks.Insert(insertAt++, Clone(textTemplate, textId));
            }

            Chunk(chunks, "TxSq", TextClass, textId).Payload = TextPayload(textHeader, DisplayName(section.Name));

            var marker = markerTemplate.ToArray();
            WriteUInt32(marker, 4, checked((uint)(EventOrigin + ToLogicTicks(section.StartTicks, score.TicksPerQuarterNote))));
            WriteUInt32(marker, 16, textId);
            marker[16 + 8] = (byte)(i % 12); // colour
            WriteUInt32(marker, 16 + 12, (uint)ToLogicTicks(Math.Max(0, end - section.StartTicks), score.TicksPerQuarterNote));
            markers.Write(marker);
        }

        markers.Write(SequenceTerminator);
        list.Payload = markers.ToArray();
        return created;
    }

    /// <summary>Plain-text marker name: header with the total length at bytes 0 and 20, then the text and a NUL.</summary>
    private static byte[] TextPayload(byte[] header, string text)
    {
        var payload = new byte[TextHeaderLength + Encoding.UTF8.GetByteCount(text) + 1];
        header.CopyTo(payload, 0);
        Encoding.UTF8.GetBytes(text, payload.AsSpan(TextHeaderLength));
        WriteUInt32(payload, 0, (uint)payload.Length);
        WriteUInt32(payload, 20, (uint)payload.Length);
        return payload;
    }

    /// <summary>YuE writes lower-case section names ("pre-chorus"); Logic's own arrangement names are capitalized.</summary>
    private static string DisplayName(string section) =>
        section.Length == 0 ? section : char.ToUpperInvariant(section[0]) + section[1..];

    private static IEnumerable<byte[]> Records(byte[] list, int length)
    {
        for (var offset = 0; offset + length <= list.Length - SequenceTerminator.Length; offset += length)
        {
            yield return list.AsSpan(offset, length).ToArray();
        }
    }

    private static LogicChunk Chunk(List<LogicChunk> chunks, string tag, ushort klass, uint id) =>
        chunks.Single(c => c.Tag == tag && c.Class == klass && c.Id == id);

    private static LogicChunk Clone(LogicChunk chunk, uint id)
    {
        var header = chunk.Header.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10, 4), id);
        return new LogicChunk(header, chunk.Payload.ToArray());
    }

    private static uint ReadUInt32(byte[] buffer, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
}
