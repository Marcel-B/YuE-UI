using YueUI.Api.Video;

namespace YueUI.Api.Tests;

/// <summary>The video's bars are computed as the app's analyzer computes them; these hold it to what that one shows.</summary>
public sealed class SpectrumBandsTests
{
    private const int Bars = 48;

    [Fact]
    public void A_tone_lights_the_bar_of_its_pitch_and_leaves_the_far_ones_at_a_sliver()
    {
        var rows = Analyze(Tone(1000, 0.5, seconds: 1));
        var last = rows[^1];

        var band = BandOf(1000);
        Assert.True(last[band] > 230, $"Bar {band}: {last[band]}");
        Assert.True(last[2] < 20, $"Bar 2: {last[2]}");
        Assert.True(last[Bars - 2] < 20, $"Bar {Bars - 2}: {last[Bars - 2]}");
    }

    [Fact]
    public void A_quiet_high_tone_still_stands_halfway_since_the_scale_is_in_decibels()
    {
        // 40 dB below full scale, about where a song's highs sit under its bass; showcqt left it flat.
        var rows = Analyze(Tone(8000, 0.01, seconds: 1));

        var height = rows[^1][BandOf(8000)];
        Assert.InRange(height, 110, 160);
    }

    [Fact]
    public void Bars_rise_at_once_and_fall_at_a_steady_pace()
    {
        var samples = Tone(1000, 0.5, seconds: 1).Concat(new float[SpectrumBands.SampleRate]).ToArray();
        var rows = Analyze(samples);
        var band = BandOf(1000);
        var loud = VideoLayout.FrameRate;

        // Within a few frames of the tone's start: the analyser's own window and smoothing, nothing more.
        Assert.True(rows[6][band] > 200, $"Frame 6: {rows[6][band]}");
        // Once the tone stops, every frame takes at most a twentieth of the height away.
        for (var frame = loud + 1; frame < rows.Count; frame++)
        {
            Assert.InRange(rows[frame - 1][band] - rows[frame][band], -1, 13);
        }
        Assert.True(rows[^1][band] < 20, $"Last: {rows[^1][band]}");
    }

    [Fact]
    public void There_is_one_row_per_frame_from_the_start()
    {
        var rows = Analyze(new float[SpectrumBands.SampleRate * 2]);

        Assert.Equal(VideoLayout.FrameRate * 2 + 1, rows.Count);
        Assert.All(rows, row => Assert.All(row, level => Assert.Equal(6, level)));
    }

    private static int BandOf(double hz) => (int)(Math.Log(hz / 30) / Math.Log(16000.0 / 30) * Bars);

    private static float[] Tone(double hz, double amplitude, double seconds) =>
        Enumerable.Range(0, (int)(SpectrumBands.SampleRate * seconds))
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / SpectrumBands.SampleRate)))
            .ToArray();

    private static List<byte[]> Analyze(float[] samples)
    {
        var analyzer = new SpectrumBands(Bars);
        using var output = new MemoryStream();
        analyzer.WriteSilence(output);
        analyzer.Push(samples, output);
        return output.ToArray().Chunk(Bars).ToList();
    }
}
