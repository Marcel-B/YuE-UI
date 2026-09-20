using System.Buffers.Binary;

namespace YueToLogic.Core.Logic;

/// <summary>
/// The object registry in the <c>Song</c> chunk. Logic lists every object (class, id) twice: in a table of 24-byte
/// entries with a time-based UUID and in a table of 16-byte entries with that UUID's timestamp. Objects the writer
/// creates (chord regions, marker texts) must be added to both, sorted after the existing objects of their class.
/// </summary>
/// <remarks>Verified by opening, editing, saving and reopening generated projects in Logic Pro 12.3.</remarks>
internal static class LogicObjectRegistry
{
    private const int UuidEntryLength = 24;
    private const int TimestampEntryLength = 16;

    /// <summary>100-ns intervals between the Gregorian calendar reform (UUID epoch) and 0001-01-01.</summary>
    private const long UuidEpochTicks = 499_163_040_000_000_000;

    public static byte[] Register(byte[] song, IReadOnlyCollection<(uint Class, uint Id)> objects, DateTimeOffset now, Random random)
    {
        var inserts = new List<(int Offset, byte[] Entries)>();
        var timestamp = now.UtcTicks - UuidEpochTicks;

        foreach (var group in objects.GroupBy(o => o.Class))
        {
            var klass = group.Key;
            var uuidTable = LastEntry(song, klass, UuidEntryLength, after: 0);
            var timestampTable = LastEntry(song, klass, TimestampEntryLength, after: uuidTable + UuidEntryLength);

            using var uuidEntries = new MemoryStream();
            using var timestampEntries = new MemoryStream();
            foreach (var id in group.Select(o => o.Id).Order())
            {
                var (uuid, uuidTime) = NewTimeBasedUuid(timestamp++, random);
                var entry = new byte[UuidEntryLength];
                BinaryPrimitives.WriteUInt32LittleEndian(entry, klass);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), id);
                uuid.CopyTo(entry, 8);
                uuidEntries.Write(entry);

                var shortEntry = new byte[TimestampEntryLength];
                BinaryPrimitives.WriteUInt32LittleEndian(shortEntry, klass);
                BinaryPrimitives.WriteUInt32LittleEndian(shortEntry.AsSpan(4), id);
                BinaryPrimitives.WriteUInt64LittleEndian(shortEntry.AsSpan(8), uuidTime);
                timestampEntries.Write(shortEntry);
            }

            inserts.Add((uuidTable + UuidEntryLength, uuidEntries.ToArray()));
            inserts.Add((timestampTable + TimestampEntryLength, timestampEntries.ToArray()));
        }

        var result = new List<byte>(song);
        foreach (var (offset, entries) in inserts.OrderByDescending(i => i.Offset))
        {
            result.InsertRange(offset, entries);
        }

        return result.ToArray();
    }

    /// <summary>Removes the entries of objects that are no longer in the project, such as its audio file.</summary>
    public static byte[] Remove(byte[] song, IReadOnlyCollection<(uint Class, uint Id)> objects)
    {
        var removals = new List<(int Offset, int Length)>();
        foreach (var (klass, id) in objects)
        {
            var uuidEntry = Entry(song, klass, id, UuidEntryLength, after: 0);
            removals.Add((uuidEntry, UuidEntryLength));
            removals.Add((Entry(song, klass, id, TimestampEntryLength, after: uuidEntry + UuidEntryLength), TimestampEntryLength));
        }

        var result = new List<byte>(song);
        foreach (var (offset, length) in removals.OrderByDescending(r => r.Offset))
        {
            result.RemoveRange(offset, length);
        }

        return result.ToArray();
    }

    /// <summary>Offset of the entry of one object, recognized like <see cref="LastEntry"/> by its neighbours.</summary>
    private static int Entry(byte[] song, uint klass, uint id, int entryLength, int after)
    {
        for (var offset = Math.Max(after, entryLength); offset + (2 * entryLength) <= song.Length; offset++)
        {
            if (ReadUInt32(song, offset) == klass
                && ReadUInt32(song, offset + 4) == id
                && ReadUInt32(song, offset - entryLength) < 0x40
                && ReadUInt32(song, offset + entryLength) < 0x40)
            {
                return offset;
            }
        }

        throw new InvalidOperationException($"The Logic template's object registry has no entry for object {klass}/{id}.");
    }

    /// <summary>
    /// Offset of the entry with the highest id of <paramref name="klass"/> in the table with the given entry length,
    /// searching from <paramref name="after"/> (the timestamp table follows the UUID table). Table entries are
    /// recognized by their neighbours, which start with small class numbers as well.
    /// </summary>
    private static int LastEntry(byte[] song, uint klass, int entryLength, int after)
    {
        var best = -1;
        var bestId = -1L;
        for (var offset = Math.Max(after, entryLength); offset + (2 * entryLength) <= song.Length; offset++)
        {
            if (ReadUInt32(song, offset) != klass)
            {
                continue;
            }

            var id = ReadUInt32(song, offset + 4);
            if (id % 4 != 0 || id >= 0x10000
                || ReadUInt32(song, offset - entryLength) >= 0x40
                || ReadUInt32(song, offset + entryLength) >= 0x40)
            {
                continue;
            }

            if (id > bestId)
            {
                best = offset;
                bestId = id;
            }
        }

        return best >= 0
            ? best
            : throw new InvalidOperationException($"The Logic template's object registry has no table entry for class {klass}.");
    }

    /// <summary>An RFC 4122 version-1 UUID (big-endian, as Logic stores it) with random clock sequence and node.</summary>
    private static (byte[] Uuid, ulong Timestamp) NewTimeBasedUuid(long timestamp, Random random)
    {
        var timeLow = (uint)timestamp;
        var timeMid = (ushort)(timestamp >> 32);
        var timeHigh = (ushort)(((timestamp >> 48) & 0x0FFF) | 0x1000);
        var uuid = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(uuid, timeLow);
        BinaryPrimitives.WriteUInt16BigEndian(uuid.AsSpan(4), timeMid);
        BinaryPrimitives.WriteUInt16BigEndian(uuid.AsSpan(6), timeHigh);
        BinaryPrimitives.WriteUInt16BigEndian(uuid.AsSpan(8), (ushort)(random.Next(0x4000) | 0x8000));
        random.NextBytes(uuid.AsSpan(10, 6));
        var packed = timeLow | ((ulong)timeMid << 32) | ((ulong)(timeHigh & 0x0FFF) << 48);
        return (uuid, packed);
    }

    private static uint ReadUInt32(byte[] buffer, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
}
