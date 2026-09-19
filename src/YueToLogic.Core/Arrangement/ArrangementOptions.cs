namespace YueToLogic.Core.Arrangement;

/// <summary>Changes applied to a parsed score before it is rendered. The defaults leave the score untouched.</summary>
public sealed record ArrangementOptions
{
    /// <summary>
    /// Octave shift per score voice, keyed by voice id (<c>Vocal</c>, <c>Ins</c>; case-insensitive).
    /// Positive values move up, negative values down.
    /// </summary>
    public IReadOnlyDictionary<string, int> OctaveShifts { get; init; } = new Dictionary<string, int>();

    /// <summary>Octave shift for every score voice that has no entry in <see cref="OctaveShifts"/>.</summary>
    public int DefaultOctaveShift { get; init; }

    /// <summary>Adds a bass line following the chord symbols; <c>null</c> for none.</summary>
    public BassOptions? Bass { get; init; }

    /// <summary>Adds a drum pattern for the whole song; <c>null</c> for none.</summary>
    public DrumOptions? Drums { get; init; }
}

public sealed record BassOptions
{
    public BassPattern Pattern { get; init; } = BassPattern.Eighths;

    /// <summary>Velocity on the beat; off-beat notes are played slightly softer.</summary>
    public int Velocity { get; init; } = 100;
}

public enum BassPattern
{
    /// <summary>Straight eighth notes on the chord's bass note.</summary>
    Eighths,

    /// <summary>Quarter notes on the chord's bass note.</summary>
    Quarters,

    /// <summary>Quarter notes alternating between the bass note and the chord's fifth.</summary>
    RootFifth,
}

public sealed record DrumOptions
{
    /// <summary>Plays a crash cymbal at the start of every section (verse, chorus, …).</summary>
    public bool CrashOnSections { get; init; } = true;
}

/// <summary>Note numbers of the General MIDI drum map, which Logic's drum kits follow.</summary>
public static class GeneralMidiDrums
{
    public const int Channel = 9;
    public const int Kick = 36;
    public const int Snare = 38;
    public const int ClosedHiHat = 42;
    public const int Crash = 49;
}
