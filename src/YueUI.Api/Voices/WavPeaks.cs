using System.Buffers.Binary;

namespace YueUI.Api.Voices;

/// <summary>
/// The loudness outline of a WAV: for each of a number of equal slices the highest absolute sample across all channels,
/// in full scale (1 = 0 dBFS). Done here rather than in the browser (<c>waveform.ts</c>), where a phone would have to
/// fetch and decode every stem of a song, tens of megabytes each, before it could draw them.
/// </summary>
public static class WavPeaks
{
    private const ushort Pcm = 1;
    private const ushort Float = 3;
    private const ushort Extensible = 0xFFFE;

    /// <returns>Null for anything but PCM (8 to 32 bits) or 32/64-bit float, the formats StemMyWav and ffmpeg write.</returns>
    public static (double Seconds, double[] Peaks)? Read(string path, int buckets)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Read(stream, buckets);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    public static (double Seconds, double[] Peaks)? Read(Stream stream, int buckets)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12
            || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            return null;
        }
        ushort format = 0, channels = 0, bits = 0;
        uint rate = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                var fmt = new byte[size];
                stream.ReadExactly(fmt);
                if (size < 16)
                {
                    return null;
                }
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                // WAVE_FORMAT_EXTENSIBLE names the real format in the first two bytes of its sub-format GUID.
                if (format == Extensible && size >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                }
                if (size % 2 == 1)
                {
                    stream.ReadByte();
                }
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                // A writer that could not seek back leaves the size at its maximum (or 0); the data then runs to the end.
                long length = size is 0 or uint.MaxValue ? stream.Length - stream.Position : Math.Min(size, stream.Length - stream.Position);
                return Scan(stream, length, format, channels, bits, rate, buckets);
            }
            else
            {
                stream.Seek(size + size % 2, SeekOrigin.Current);
            }
        }
        return null;
    }

    private static (double Seconds, double[] Peaks)? Scan(Stream stream, long length, ushort format, ushort channels, ushort bits, uint rate, int buckets)
    {
        var width = bits / 8;
        var valid = format switch
        {
            Pcm => bits is 8 or 16 or 24 or 32,
            Float => bits is 32 or 64,
            _ => false,
        };
        if (!valid || channels == 0 || rate == 0 || buckets <= 0)
        {
            return null;
        }
        var frameSize = width * channels;
        var frames = length / frameSize;
        var peaks = new double[buckets];
        if (frames == 0)
        {
            return (0, peaks);
        }
        var perBucket = Math.Max(1, (frames + buckets - 1) / buckets);
        var buffer = new byte[frameSize * 4096];
        long frame = 0;
        while (frame < frames)
        {
            var wanted = (int)Math.Min(buffer.Length / frameSize, frames - frame) * frameSize;
            var read = stream.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            if (read < frameSize)
            {
                break;
            }
            for (var offset = 0; offset + frameSize <= read; offset += frameSize, frame++)
            {
                var bucket = (int)Math.Min(buckets - 1, frame / perBucket);
                var peak = peaks[bucket];
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = Math.Abs(Sample(buffer.AsSpan(offset + channel * width, width), format, bits));
                    if (value > peak)
                    {
                        peak = value;
                    }
                }
                peaks[bucket] = Math.Min(1, peak);
            }
        }
        return ((double)frames / rate, peaks);
    }

    private static double Sample(ReadOnlySpan<byte> bytes, ushort format, ushort bits) => (format, bits) switch
    {
        (Float, 32) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        (Float, _) => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        // 8-bit WAV is the one unsigned format.
        (_, 8) => (bytes[0] - 128) / 128.0,
        (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
        (_, 24) => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608.0,
        _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
    };
}
