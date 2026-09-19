using System.Globalization;
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

    /// <returns>One error diagnostic per invalid value; empty if the options are valid.</returns>
    public static IReadOnlyList<Diagnostic> Validate(ConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new DiagnosticBag();

        if (options.TicksPerQuarterNote is < MinTicksPerQuarterNote or > MaxTicksPerQuarterNote)
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"ticksPerQuarterNote must be between {MinTicksPerQuarterNote} and {MaxTicksPerQuarterNote}, got {options.TicksPerQuarterNote}."));
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

        if (arrangement.Drums is { } drums && !Enum.IsDefined(drums.Pattern))
        {
            errors.Error(DiagnosticCodes.InvalidOption, Invariant($"arrangement.drums.pattern '{drums.Pattern}' is not supported."));
        }

        return errors.ToList();
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
