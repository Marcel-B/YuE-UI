using System.Globalization;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Conversion;

/// <summary>Adjusts the tempo so that the score lasts exactly as long as a recording of it.</summary>
/// <remarks>
/// YuE's audio and its symbolic score do not always agree on how long the song is. Where the difference is a
/// fraction of a percent the score is simply a little off, and stretching the tempo makes audio and MIDI line
/// up over the whole song instead of drifting apart towards the end.
///
/// A large difference means something else: the audio was cut short at a generation limit, or it is not the
/// recording of this score at all. Fitting the tempo would then compress or stretch the whole song to a length
/// the music never had. Anything beyond <see cref="TempoFitOptions.MaxDeviation"/> is therefore reported and
/// left alone rather than silently applied.
/// </remarks>
public sealed record TempoFitOptions
{
    /// <summary>Length of the recording in seconds; the host measures it, the library only does the arithmetic.</summary>
    public double AudioSeconds { get; set; }

    /// <summary>
    /// How far the score may be off before the difference is taken for something other than a drift, as a
    /// fraction: 0.05 accepts a score up to five percent longer or shorter than its recording.
    /// </summary>
    public double MaxDeviation { get; set; } = 0.05;
}

internal static class TempoFitter
{
    /// <summary>Lowest and highest tempo a fit may produce; beyond these a MIDI file stops being useful.</summary>
    private const double MinTempo = 20;
    private const double MaxTempo = 400;

    public static ScoreDocument Fit(ScoreDocument score, TempoFitOptions options, DiagnosticBag diagnostics)
    {
        var audio = options.AudioSeconds;
        if (audio <= 0)
        {
            diagnostics.Warning(DiagnosticCodes.TempoNotFitted, "The tempo cannot be fitted: the audio length is not known.");
            return score;
        }

        // The count-in is not part of the recording, so only the music itself is compared.
        var music = score.MusicDurationSeconds;
        if (music <= 0)
        {
            diagnostics.Warning(DiagnosticCodes.TempoNotFitted, "The tempo cannot be fitted: the score has no length.");
            return score;
        }

        var ratio = music / audio;
        var deviation = Math.Abs(ratio - 1);
        if (deviation > Math.Max(0, options.MaxDeviation))
        {
            diagnostics.Warning(
                DiagnosticCodes.TempoNotFitted,
                Invariant($"The tempo was not fitted: the score lasts {music:0.0} s but the audio {audio:0.0} s, a difference of {deviation * 100:0.#} %. That is more than a drift; the audio was probably cut short, or it belongs to another take. The tempo stays at {score.TempoBpm:0.###} BPM."));
            return score;
        }

        // Four decimals is what Logic stores (BPM x 10000), so the fitted tempo survives the export exactly.
        // What the rounding leaves behind is microseconds over a whole song.
        var tempo = Math.Round(score.TempoBpm * ratio, 4, MidpointRounding.AwayFromZero);
        if (tempo is < MinTempo or > MaxTempo)
        {
            diagnostics.Warning(
                DiagnosticCodes.TempoNotFitted,
                Invariant($"The tempo was not fitted: {tempo:0.###} BPM is outside the usable range of {MinTempo} to {MaxTempo} BPM."));
            return score;
        }

        if (Math.Abs(tempo - score.TempoBpm) < 0.0001)
        {
            return score;
        }

        diagnostics.Info(
            DiagnosticCodes.TempoFitted,
            Invariant($"Tempo fitted to the audio: {score.TempoBpm:0.###} → {tempo:0.###} BPM, so the score's {music:0.0} s become the audio's {audio:0.0} s."));
        return score with { TempoBpm = tempo };
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
