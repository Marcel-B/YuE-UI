using System.Buffers.Binary;
using System.Text;

namespace YueToLogic.Core.Logic;

/// <summary>
/// The binary <c>Alternatives/000/ProjectData</c> file of a Logic Pro package, as a list of chunks.
/// </summary>
/// <remarks>
/// The format is undocumented; this model covers what the YuE template needs and was verified against
/// projects saved by Logic Pro 12.3. A file is a 24-byte header (bytes 16–19: length of everything after it)
/// followed by chunks. Every chunk has a 36-byte header — a reversed four-character tag (e.g. <c>qSvE</c> for
/// "EvSq"), a class at byte 6, an object id at byte 10 and the payload length as UInt64 at byte 28 — and its payload.
/// </remarks>
internal sealed class LogicProjectData
{
    public const int FileHeaderLength = 24;
    public const int ChunkHeaderLength = 36;

    private readonly byte[] _fileHeader;

    private LogicProjectData(byte[] fileHeader, List<LogicChunk> chunks)
    {
        _fileHeader = fileHeader;
        Chunks = chunks;
    }

    public List<LogicChunk> Chunks { get; }

    public static LogicProjectData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileHeaderLength)
        {
            throw new InvalidDataException("ProjectData is shorter than its header.");
        }

        var chunks = new List<LogicChunk>();
        var offset = FileHeaderLength;
        while (offset < data.Length)
        {
            if (data.Length - offset < ChunkHeaderLength)
            {
                throw new InvalidDataException($"Truncated chunk header at offset {offset}.");
            }

            var length = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 28, 8));
            if (length > (ulong)(data.Length - offset - ChunkHeaderLength))
            {
                throw new InvalidDataException($"Chunk at offset {offset} claims {length} bytes beyond the end of the file.");
            }

            var header = data.Slice(offset, ChunkHeaderLength).ToArray();
            var payload = data.Slice(offset + ChunkHeaderLength, (int)length).ToArray();
            chunks.Add(new LogicChunk(header, payload));
            offset += ChunkHeaderLength + (int)length;
        }

        return new LogicProjectData(data[..FileHeaderLength].ToArray(), chunks);
    }

    public byte[] Serialize()
    {
        var bodyLength = Chunks.Sum(c => ChunkHeaderLength + c.Payload.Length);
        var result = new byte[FileHeaderLength + bodyLength];
        _fileHeader.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), (uint)bodyLength);

        var offset = FileHeaderLength;
        foreach (var chunk in Chunks)
        {
            chunk.Header.CopyTo(result, offset);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset + 28, 8), (ulong)chunk.Payload.Length);
            chunk.Payload.CopyTo(result, offset + ChunkHeaderLength);
            offset += ChunkHeaderLength + chunk.Payload.Length;
        }

        return result;
    }
}

internal sealed class LogicChunk(byte[] header, byte[] payload)
{
    public byte[] Header { get; } = header;

    public byte[] Payload { get; set; } = payload;

    /// <summary>The tag in reading order, e.g. <c>EvSq</c> (stored reversed as <c>qSvE</c>).</summary>
    public string Tag { get; } = new(Encoding.ASCII.GetString(header, 0, 4).Reverse().ToArray());

    public ushort Class => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(6, 2));

    public uint Id => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(10, 4));

    /// <summary>Name of an <c>MSeq</c> (sequence/region): UInt16 length at byte 16, then Latin-1 text.</summary>
    public string SequenceName
    {
        get
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(Payload.AsSpan(16, 2));
            return Encoding.Latin1.GetString(Payload, 18, length);
        }
    }

    /// <summary>Offset of the length field of an <c>MSeq</c>: after the name, padded to an even size, plus 60 bytes.</summary>
    public int SequenceLengthOffset
    {
        get
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(Payload.AsSpan(16, 2));
            return 18 + nameLength + (nameLength & 1) + 60;
        }
    }
}
