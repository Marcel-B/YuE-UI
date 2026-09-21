using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

/// <summary>
/// The numbers below come from real YuE runs. They fall into two groups: scores that are a fraction of a
/// percent off their recording, which is the drift the fit is for, and recordings that stop at a generation
/// limit long before the score ends, where fitting would compress the whole song to a length it never had.
/// </summary>
public class TempoFitTests
{
    private static ConversionOptions Fit(double audioSeconds, double? maxDeviation = null)
    {
        var options = new ConversionOptions { FitTempo = new TempoFitOptions { AudioSeconds = audioSeconds } };
        if (maxDeviation is { } max)
        {
            options.FitTempo!.MaxDeviation = max;
        }

        return options;
    }

    [Fact]
    public void A_score_slightly_longer_than_its_recording_is_pulled_onto_it()
    {
        // The sample is 8 bars at 88 BPM, which is 21.818 s; a recording of 21.0 s means it ran a little faster.
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), Fit(21.0));

        Assert.True(result.Success);
        // The tempo is rounded to the four decimals Logic stores, which leaves microseconds on the table.
        Assert.Equal(21.0, result.Score!.DurationSeconds, 3);
        Assert.Equal(91.4286, result.Score.TempoBpm, 4);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.TempoFitted && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void A_recording_cut_short_is_reported_and_the_tempo_left_alone()
    {
        // 8 bars at 88 BPM against 15 s of audio: a third short, which no drift explains.
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), Fit(15.0));

        Assert.True(result.Success);
        Assert.Equal(88, result.Score!.TempoBpm);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == DiagnosticCodes.TempoNotFitted);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("cut short", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Score seconds, audio seconds, whether the fit applies - taken from the runs in ~/Music/YuE Studio.
    [InlineData(354.29, 352.68, true)] // 0.5 % out: the drift this is for
    [InlineData(341.14, 338.80, true)] // 0.7 % out
    [InlineData(309.04, 309.16, true)] // as good as exact
    [InlineData(368.12, 300.00, false)] // audio stopped at the 300 s limit, 37 bars early
    [InlineData(475.20, 360.00, false)] // audio stopped at the 360 s limit
    [InlineData(308.24, 360.00, false)] // audio longer than the score: not this take
    public void The_guard_separates_a_drift_from_a_recording_that_does_not_match(double score, double audio, bool fits)
    {
        // A score whose length is exactly the given number of seconds at 120 BPM.
        var quarters = (int)Math.Round(score * 120 / 60);
        var bars = quarters / 4;
        var abc = Native(string.Join("\n", Enumerable.Repeat("C16|", bars)), tempo: "1/4=120");

        var result = new ScoreConverter().Convert(abc, Fit(audio));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        if (fits)
        {
            Assert.Equal(audio, result.Score!.DurationSeconds, 1);
            Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.TempoFitted);
        }
        else
        {
            Assert.Equal(120, result.Score!.TempoBpm);
            Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.TempoNotFitted);
        }
    }

    [Fact]
    public void The_guard_can_be_widened_for_a_recording_the_user_knows_is_right()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), Fit(15.0, maxDeviation: 0.5));

        Assert.Equal(15.0, result.Score!.DurationSeconds, 3);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.TempoFitted);
    }

    [Fact]
    public void A_count_in_is_left_out_of_the_comparison()
    {
        // The recording holds the music, not the silence in front of it.
        var options = Fit(21.0);
        options.Arrangement = new ArrangementOptions { CountIn = new CountInOptions { Bars = 2 } };

        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);

        Assert.Equal(21.0, result.Score!.MusicDurationSeconds, 3);
        Assert.Equal(91.4286, result.Score.TempoBpm, 4);

        // The whole document is longer by exactly the two lead-in bars.
        Assert.Equal(21.0 + (2 * 4 * 60 / result.Score.TempoBpm), result.Score.DurationSeconds, 3);
    }

    [Fact]
    public void An_unusable_audio_length_is_rejected_as_an_invalid_option()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), Fit(0));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.InvalidOption);
    }
}
