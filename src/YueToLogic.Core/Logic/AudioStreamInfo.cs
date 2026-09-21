using System.Buffers.Binary;

namespace YueToLogic.Core.Logic;

/// <summary>What a Logic project needs to know about an audio file: its format and how long it is.</summary>
/// <remarks>
/// YuE writes its recording as FLAC, StemMyWav its stems as WAV. A track of the Logic template is prepared for
/// one of the two, since the format is part of the audio file object, so both are read here.
/// </remarks>
public sealed record AudioStreamInfo(AudioFormat Format, int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    /// <summary>
    /// Enough for both headers: "fLaC" with its STREAMINFO needs 42 bytes, a RIFF file its fmt and data chunks,
    /// which a writer may put behind other chunks such as a LIST of tags.
    /// </summary>
    public const int HeaderLength = 1024;

    public double DurationSeconds => SampleRate == 0 ? 0 : TotalSamples / (double)SampleRate;

    public static bool TryParse(ReadOnlySpan<byte> header, out AudioStreamInfo? info) =>
        TryParseFlac(header, out info) || TryParseWave(header, out info);

    /// <summary>The STREAMINFO block every FLAC file starts with.</summary>
    private static bool TryParseFlac(ReadOnlySpan<byte> header, out AudioStreamInfo? info)
    {
        info = null;
        // The first metadata block must be STREAMINFO (type 0, lower seven bits of byte 4).
        if (header.Length < 42 || !header[..4].SequenceEqual("fLaC"u8) || (header[4] & 0x7F) != 0)
        {
            return false;
        }

        var body = header.Slice(8, 34);
        var sampleRate = (body[10] << 12) | (body[11] << 4) | (body[12] >> 4);
        if (sampleRate == 0)
        {
            return false;
        }

        info = new AudioStreamInfo(
            AudioFormat.Flac,
            sampleRate,
            ((body[12] >> 1) & 0x07) + 1,
            (((body[12] & 0x01) << 4) | (body[13] >> 4)) + 1,
            ((long)(body[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(body.Slice(14, 4)));
        return true;
    }

    /// <summary>A RIFF file of WAVE chunks: the format comes from its fmt chunk, the length from its data chunk.</summary>
    private static bool TryParseWave(ReadOnlySpan<byte> header, out AudioStreamInfo? info)
    {
        info = null;
        if (header.Length < 36 || !header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        // Chunks follow the "WAVE" tag, each with a four-character tag and its length.
        long dataBytes = 0;
        for (var offset = 12; offset + 8 <= header.Length;)
        {
            var tag = header.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset + 4, 4));
            if (tag.SequenceEqual("data"u8))
            {
                dataBytes = length;
                break;
            }

            if (tag.SequenceEqual("fmt "u8) && offset + 8 + 16 <= header.Length)
            {
                var body = header.Slice(offset + 8, 16);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
                var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
                var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(14, 2));
                if (sampleRate == 0 || channels == 0 || bitsPerSample == 0)
                {
                    return false;
                }

                info = new AudioStreamInfo(AudioFormat.Wave, sampleRate, channels, bitsPerSample, TotalSamples: 0);
            }

            offset += 8 + (int)length + ((int)length & 1);
        }

        info = info?.WithDataLength(dataBytes);
        return info is not null;
    }

    /// <summary>The number of samples a WAVE file of this format holds in <paramref name="bytes"/> of audio.</summary>
    public AudioStreamInfo WithDataLength(long bytes)
    {
        var frame = Channels * ((BitsPerSample + 7) / 8);
        return frame == 0 ? this : this with { TotalSamples = bytes / frame };
    }
}

public enum AudioFormat
{
    Flac,
    Wave,
}
