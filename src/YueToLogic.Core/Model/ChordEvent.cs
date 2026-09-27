namespace YueToLogic.Core.Model;

/// <param name="Text">The chord symbol exactly as written in the score.</param>
/// <param name="Symbol">The parsed chord, or <c>null</c> if the text could not be interpreted at all.</param>
public sealed record ChordEvent(long StartTicks, long DurationTicks, string Text, ChordSymbol? Symbol);

public sealed record ChordSymbol(int RootPitchClass, ChordQuality Quality, int? BassPitchClass);

/// <summary>The closed chord vocabulary of the YuE2 ABC dialect.</summary>
public enum ChordQuality
{
    Major,
    Minor,
    Diminished,
    Augmented,
    Dominant7,
    Major7,
    Minor7,
    Diminished7,
    HalfDiminished7,
    Suspended4,
    Suspended2,
    Major6,
    Minor6,
    Dominant7Suspended4,
    MinorMajor7,
}
