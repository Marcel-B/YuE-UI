using System.Buffers.Binary;

namespace YueToLogic.Core.Logic;

/// <summary>The STREAMINFO block every FLAC file starts with (format, length).</summary>
public sealed record FlacStreamInfo(int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    /// <summary>"fLaC", the metadata block header and the 34-byte STREAMINFO body.</summary>
    public const int HeaderLength = 4 + 4 + 34;

    public double DurationSeconds => SampleRate == 0 ? 0 : TotalSamples / (double)SampleRate;

    /// <summary>Reads the stream info from the first <see cref="HeaderLength"/> bytes of a FLAC file.</summary>
    public static bool TryParse(ReadOnlySpan<byte> header, out FlacStreamInfo? info)
    {
        info = null;
        // The first metadata block must be STREAMINFO (type 0, lower seven bits of byte 4).
        if (header.Length < HeaderLength || !header[..4].SequenceEqual("fLaC"u8) || (header[4] & 0x7F) != 0)
        {
            return false;
        }

        var body = header.Slice(8, 34);
        var sampleRate = (body[10] << 12) | (body[11] << 4) | (body[12] >> 4);
        var channels = ((body[12] >> 1) & 0x07) + 1;
        var bitsPerSample = (((body[12] & 0x01) << 4) | (body[13] >> 4)) + 1;
        var totalSamples = ((long)(body[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(body.Slice(14, 4));
        if (sampleRate == 0)
        {
            return false;
        }

        info = new FlacStreamInfo(sampleRate, channels, bitsPerSample, totalSamples);
        return true;
    }
}
