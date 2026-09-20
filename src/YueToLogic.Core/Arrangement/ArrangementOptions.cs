namespace YueToLogic.Core.Arrangement;

/// <summary>Changes applied to a parsed score before it is rendered. The defaults leave the score untouched.</summary>
public sealed record ArrangementOptions
{
    /// <summary>
    /// Octave shift per score voice, keyed by voice id (<c>Vocal</c>, <c>Ins</c>; case-insensitive).
    /// Positive values move up, negative values down.
    /// </summary>
    public IReadOnlyDictionary<string, int> OctaveShifts { get; set; } = new Dictionary<string, int>();

    /// <summary>Octave shift for every score voice that has no entry in <see cref="OctaveShifts"/>.</summary>
    public int DefaultOctaveShift { get; set; }

    /// <summary>Adds a bass line following the chord symbols; <c>null</c> for none.</summary>
    public BassOptions? Bass { get; set; }

    /// <summary>Adds a drum pattern for the whole song; <c>null</c> for none.</summary>
    public DrumOptions? Drums { get; set; }

    /// <summary>
    /// How the chord symbols are played on the chord track. Without it the score keeps no chord track of its own
    /// and the chords are rendered as written, one sustained block chord per symbol.
    /// </summary>
    public ChordOptions? Chords { get; set; }
}

public sealed record ChordOptions
{
    public ChordPattern Pattern { get; set; } = ChordPattern.Block;

    /// <summary>Octaves relative to the default register, whose root lies in octave 3 (MIDI 48, C2 in Logic).</summary>
    public int OctaveShift { get; set; }

    /// <summary>Velocity on the beat; notes off the beat are played slightly softer.</summary>
    public int Velocity { get; set; } = 72;
}

public enum ChordPattern
{
    /// <summary>One sustained chord per symbol, as written.</summary>
    Block,

    /// <summary>The whole chord repeated on every eighth note.</summary>
    Eighths,

    /// <summary>Short chords on the off-beats only, the usual pop and reggae "skank".</summary>
    Offbeat,

    /// <summary>The notes of the chord one after another in eighths, from the bottom up.</summary>
    ArpeggioUp,
}

public sealed record BassOptions
{
    public BassPattern Pattern { get; set; } = BassPattern.Eighths;

    /// <summary>
    /// Octaves relative to the default register E2–D#3 (MIDI 40–51, E1–D#2 in Logic's naming);
    /// -1 is the bottom octave of a four-string bass guitar.
    /// </summary>
    public int OctaveShift { get; set; }

    /// <summary>Velocity on the beat; off-beat notes are played slightly softer.</summary>
    public int Velocity { get; set; } = 100;
}

public enum BassPattern
{
    /// <summary>Straight eighth notes on the chord's bass note.</summary>
    Eighths,

    /// <summary>Quarter notes on the chord's bass note.</summary>
    Quarters,

    /// <summary>Quarter notes alternating between the bass note and the chord's fifth.</summary>
    RootFifth,

    /// <summary>Eighth notes alternating between the bass note and the octave above it.</summary>
    Octaves,

    /// <summary>Eighth notes on the off-beats only.</summary>
    Offbeat,

    /// <summary>One long note per chord.</summary>
    Sustained,
}

public sealed record DrumOptions
{
    public DrumPattern Pattern { get; set; } = DrumPattern.FourOnTheFloor;

    /// <summary>Plays a crash cymbal at the start of every section (verse, chorus, …).</summary>
    public bool CrashOnSections { get; set; } = true;
}

public enum DrumPattern
{
    /// <summary>Kick on every beat, snare on 2 and 4, closed hi-hat in eighth notes.</summary>
    FourOnTheFloor,

    /// <summary>Kick on 1 and 3, snare on 2 and 4, eighth-note hi-hat that opens on the last eighth of the bar.</summary>
    Backbeat,

    /// <summary>Kick on 1, snare on 3 alone, eighth-note hi-hat: the same groove at half the speed.</summary>
    HalfTime,

    /// <summary>Kick on every beat, snare on 2 and 4, and an open hi-hat on every off-beat.</summary>
    Disco,
}

/// <summary>Note numbers of the General MIDI drum map, which Logic's drum kits follow.</summary>
public static class GeneralMidiDrums
{
    public const int Channel = 9;
    public const int Kick = 36;
    public const int Snare = 38;
    public const int ClosedHiHat = 42;
    public const int OpenHiHat = 46;
    public const int Crash = 49;
}
