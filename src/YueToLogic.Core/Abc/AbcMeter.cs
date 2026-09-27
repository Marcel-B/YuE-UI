using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace YueToLogic.Core.Abc;

/// <summary>A time signature from an <c>M:</c> field.</summary>
internal sealed record AbcMeter(int Numerator, int Denominator)
{
    public static readonly AbcMeter CommonTime = new(4, 4);

    /// <summary>Accepts <c>4/4</c>, <c>6/8</c>, <c>C</c> (4/4) and <c>C|</c> (2/2).</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out AbcMeter? meter)
    {
        meter = null;
        var value = text.Trim();
        switch (value)
        {
            case "C":
                meter = CommonTime;
                return true;
            case "C|":
                meter = new AbcMeter(2, 2);
                return true;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0
            || !int.TryParse(value.AsSpan(0, slash).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
            || !int.TryParse(value.AsSpan(slash + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
            || numerator is < 1 or > 255
            || denominator is < 1 or > 1024
            || !BitOperations.IsPow2(denominator))
        {
            return false;
        }

        meter = new AbcMeter(numerator, denominator);
        return true;
    }

    /// <summary>Length of one measure in ticks; <c>false</c> if it is not a whole number of ticks.</summary>
    public bool TryGetMeasureTicks(int ticksPerQuarterNote, out long measureTicks)
    {
        var numerator = 4L * ticksPerQuarterNote * Numerator;
        measureTicks = numerator / Denominator;
        return numerator % Denominator == 0;
    }

    public override string ToString() => $"{Numerator}/{Denominator}";
}
