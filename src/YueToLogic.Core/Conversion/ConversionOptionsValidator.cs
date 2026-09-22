using System.Globalization;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Diagnostics;

namespace YueToLogic.Core.Conversion;

/// <summary>
/// Range checks for <see cref="ConversionOptions"/>, shared by all hosts so that a web request,
/// the CLI and a desktop app accept exactly the same values.
/// </summary>
public static class ConversionOptionsValidator
{
    public const int MinTicksPerQuarterNote = 24;
    public const int MaxTicksPerQuarterNote = short.MaxValue;
    public const int MaxOctaveShift = 4;
    public const int MaxBassOctaveShift = 2;
    public const int MaxChordOctaveShift = 2;
    public const int MaxGuideToneOctaveShift = 2;
    public const int MaxDoublingSemitones = 24;
    public const double MaxHumanizeTimingMs = 200;
    public const int MaxHumanizeVelocity = 64;
    public const double MaxMonoGapMs = 500;
    public const double MaxMonoLengthMs = 2000;
    public const int MaxCountInBars = 8;

    /// <summary>The widest difference between score and audio a tempo fit may be asked to accept.</summary>
    public const double MaxTempoDeviation = 0.5;

    /// <returns>One error diagnostic per invalid value; empty if the options are valid.</returns>
    public static IReadOnlyList<Diagnostic> Validate(ConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new DiagnosticBag();

        if (options.TicksPerQuarterNote is < MinTicksPerQuarterNote or > MaxTicksPerQuarterNote)
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"ticksPerQuarterNote must be between {MinTicksPerQuarterNote} and {MaxTicksPerQuarterNote}, got {options.TicksPerQuarterNote}."));
        }

        foreach (var (track, channel) in options.MidiChannels ?? new Dictionary<string, int>())
        {
            if (channel is < 1 or > 16)
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"midiChannels.{track} must be between 1 and 16, got {channel}."));
            }
        }

        foreach (var (track, program) in options.MidiPrograms ?? new Dictionary<string, int>())
        {
            if (program is < 1 or > 128)
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"midiPrograms.{track} must be between 1 and 128, got {program}."));
            }
        }

        var arrangement = options.Arrangement ?? new();
        CheckOctave(errors, "arrangement.defaultOctaveShift", arrangement.DefaultOctaveShift, MaxOctaveShift);
        foreach (var (voice, octaves) in arrangement.OctaveShifts ?? new Dictionary<string, int>())
        {
            CheckOctave(errors, $"arrangement.octaveShifts.{voice}", octaves, MaxOctaveShift);
        }

        if (arrangement.Bass is { } bass)
        {
            CheckOctave(errors, "arrangement.bass.octaveShift", bass.OctaveShift, MaxBassOctaveShift);
            if (bass.Velocity is < 1 or > 127)
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.bass.velocity must be between 1 and 127, got {bass.Velocity}."));
            }

            if (!Enum.IsDefined(bass.Pattern))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.bass.pattern '{bass.Pattern}' is not supported."));
            }
        }

        if (arrangement.Chords is { } chords)
        {
            CheckOctave(errors, "arrangement.chords.octaveShift", chords.OctaveShift, MaxChordOctaveShift);
            if (chords.Velocity is < 1 or > 127)
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.chords.velocity must be between 1 and 127, got {chords.Velocity}."));
            }

            if (!Enum.IsDefined(chords.Pattern))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.chords.pattern '{chords.Pattern}' is not supported."));
            }

            if (!Enum.IsDefined(chords.Inversion))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.chords.inversion '{chords.Inversion}' is not supported."));
            }
        }

        if (arrangement.Drums is { } drums)
        {
            if (!Enum.IsDefined(drums.Pattern))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.drums.pattern '{drums.Pattern}' is not supported."));
            }

            if (drums.Notes is { } notes)
            {
                foreach (var drum in Enum.GetValues<Drum>())
                {
                    CheckNote(errors, $"arrangement.drums.notes.{DrumNotes.JsonName(drum)}", notes.Of(drum));
                }
            }
        }

        if (arrangement.GuideTones is { } guideTones)
        {
            CheckOctave(errors, "arrangement.guideTones.octaveShift", guideTones.OctaveShift, MaxGuideToneOctaveShift);
            CheckVelocity(errors, "arrangement.guideTones.velocity", guideTones.Velocity);
        }

        if (arrangement.Doubling is { } doubling)
        {
            if (string.IsNullOrWhiteSpace(doubling.VoiceId))
            {
                errors.Error(DiagnosticCodes.InvalidOption, "arrangement.doubling.voiceId must name a voice of the score.");
            }

            if (doubling.Semitones < -MaxDoublingSemitones || doubling.Semitones > MaxDoublingSemitones)
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.doubling.semitones must be between -{MaxDoublingSemitones} and {MaxDoublingSemitones}, got {doubling.Semitones}."));
            }

            if (doubling.Velocity is { } velocity)
            {
                CheckVelocity(errors, "arrangement.doubling.velocity", velocity);
            }
        }

        if (arrangement.Groove is { } groove)
        {
            if (groove.Swing is < 0 or > 1 || double.IsNaN(groove.Swing))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.groove.swing must be between 0 and 1, got {groove.Swing}."));
            }

            if (!Enum.IsDefined(groove.SwingUnit))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.groove.swingUnit '{groove.SwingUnit}' is not supported."));
            }

            CheckRange(errors, "arrangement.groove.humanizeTimingMs", groove.HumanizeTimingMs, 0, MaxHumanizeTimingMs);
            CheckRange(errors, "arrangement.groove.humanizeVelocity", groove.HumanizeVelocity, 0, MaxHumanizeVelocity);
            CheckVelocity(errors, "arrangement.groove.baseVelocity", groove.BaseVelocity);
        }

        if (arrangement.Mono is { } mono)
        {
            CheckRange(errors, "arrangement.mono.gapMs", mono.GapMs, 0, MaxMonoGapMs);
            CheckRange(errors, "arrangement.mono.minimumLengthMs", mono.MinimumLengthMs, 0, MaxMonoLengthMs);
        }

        if (arrangement.CountIn is { } countIn)
        {
            if (countIn.Bars is < 0 or > MaxCountInBars)
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.countIn.bars must be between 0 and {MaxCountInBars}, got {countIn.Bars}."));
            }

            if (countIn.Note is { } note)
            {
                CheckNote(errors, "arrangement.countIn.note", note);
            }

            CheckVelocity(errors, "arrangement.countIn.velocity", countIn.Velocity);
            CheckVelocity(errors, "arrangement.countIn.accentVelocity", countIn.AccentVelocity);
        }

        if (options.FitTempo is { } fit)
        {
            if (fit.AudioSeconds <= 0 || double.IsNaN(fit.AudioSeconds) || double.IsInfinity(fit.AudioSeconds))
            {
                errors.Error(DiagnosticCodes.InvalidOption, Invariant($"fitTempo.audioSeconds must be a positive number of seconds, got {fit.AudioSeconds}."));
            }

            CheckRange(errors, "fitTempo.maxDeviation", fit.MaxDeviation, 0, MaxTempoDeviation);
        }

        return errors.ToList();
    }

    private static void CheckNote(DiagnosticBag errors, string name, int value)
    {
        if (value is < 0 or > 127)
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"{name} must be between 0 and 127, got {value}."));
        }
    }

    private static void CheckVelocity(DiagnosticBag errors, string name, int value)
    {
        if (value is < 1 or > 127)
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"{name} must be between 1 and 127, got {value}."));
        }
    }

    private static void CheckRange(DiagnosticBag errors, string name, double value, double min, double max)
    {
        if (double.IsNaN(value) || value < min || value > max)
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"{name} must be between {min} and {max}, got {value}."));
        }
    }

    private static void CheckOctave(DiagnosticBag errors, string name, int value, int max)
    {
        if (value < -max || value > max)
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"{name} must be between -{max} and {max}, got {value}."));
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
