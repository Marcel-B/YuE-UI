using System.Buffers.Binary;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class WavPeaksTests
{
    [Fact]
    public void Sixteen_bit_PCM_is_read_in_full_scale()
    {
        var (seconds, peaks) = WavPeaks.Read(new MemoryStream(FakeStems.Wav(0.5)), 10)!.Value;

        Assert.Equal(1, seconds);
        Assert.All(peaks, p => Assert.Equal(0.5, p, 3));
    }

    [Fact]
    public void Float_WAV_in_an_extensible_header_is_read_and_each_slice_keeps_its_own_peak()
    {
        // Two frames per slice: 0.25 then silence, -0.75 twice.
        var wav = Wav(format: 0xFFFE, bits: 32, channels: 1, samples: [0.25f, 0f, -0.75f, 0.1f], subFormat: 3);

        var (seconds, peaks) = WavPeaks.Read(new MemoryStream(wav), 2)!.Value;

        Assert.Equal(4 / 8000.0, seconds);
        Assert.Equal([0.25, 0.75], peaks);
    }

    [Fact]
    public void Anything_but_a_WAV_gives_no_waveform()
    {
        Assert.Null(WavPeaks.Read(new MemoryStream([.. "RIFF"u8, 5]), 10));
        Assert.Null(WavPeaks.Read(new MemoryStream("fLaC and more bytes"u8.ToArray()), 10));
    }

    private static byte[] Wav(ushort format, ushort bits, ushort channels, float[] samples, ushort subFormat)
    {
        var extensible = format == 0xFFFE;
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVEfmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write(format);
        writer.Write(channels);
        writer.Write(8000);
        writer.Write(8000 * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8));
        writer.Write(bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write(bits);
            writer.Write(0);
            writer.Write(subFormat);
            writer.Write(new byte[14]);
        }
        // A chunk the reader has to step over.
        writer.Write("LIST"u8);
        writer.Write(3);
        writer.Write(new byte[4]);
        writer.Write("data"u8);
        writer.Write(samples.Length * 4);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
