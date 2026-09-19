namespace YueToLogic.Core.Abc;

/// <summary>An ABC note length as a multiple of the unit length <c>L:</c>, e.g. <c>8</c> or <c>3/2</c>.</summary>
internal readonly record struct AbcLength(int Numerator, int Denominator)
{
    private static readonly HashSet<int> NativeMultipliers = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48];

    /// <summary>Whether the YuE2 exporter can produce this length.</summary>
    public bool IsNative => Denominator == 1 && NativeMultipliers.Contains(Numerator);

    public override string ToString() => Denominator == 1 ? $"{Numerator}" : $"{Numerator}/{Denominator}";
}

/// <param name="Column">0-based position of the token in the music line.</param>
internal abstract record AbcToken(int Column);

internal sealed record ChordSymbolToken(int Column, string Symbol) : AbcToken(Column);

internal sealed record InlineKeyToken(int Column, string Key) : AbcToken(Column);

/// <param name="Letter">Upper-case note letter, used for key signature and bar accidentals.</param>
/// <param name="WrittenPitch">MIDI pitch of the letter and octave marks, before any accidental.</param>
/// <param name="Accidental">Explicit alteration in semitones (<c>=</c> is 0), or <c>null</c> if none is written.</param>
internal sealed record NoteToken(int Column, char Letter, int WrittenPitch, int? Accidental, AbcLength Length, bool Tied)
    : AbcToken(Column);

internal sealed record RestToken(int Column, AbcLength Length) : AbcToken(Column);

/// <summary><c>Z</c>, <c>Z2</c> … : whole resting measures in the current meter.</summary>
internal sealed record MeasureRestToken(int Column, int Measures) : AbcToken(Column);

internal sealed record BarLineToken(int Column) : AbcToken(Column);

internal sealed record UnsupportedToken(int Column, string Text, string Reason) : AbcToken(Column);
