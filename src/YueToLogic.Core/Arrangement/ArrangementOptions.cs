using YueToLogic.Core.Harmony;

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

    /// <summary>Adds a sustained guide-tone track (third and seventh of every chord); <c>null</c> for none.</summary>
    public GuideToneOptions? GuideTones { get; set; }

    /// <summary>Doubles a score voice at another octave on a track of its own; <c>null</c> for none.</summary>
    public DoublingOptions? Doubling { get; set; }

    /// <summary>Swing and humanization applied to every track once it is generated; <c>null</c> leaves the timing exact.</summary>
    public GrooveOptions? Groove { get; set; }

    /// <summary>
    /// Makes the melodic tracks playable by a monophonic synthesizer; <c>null</c> leaves the notes as they are.
    /// Applied last, so its guarantees also hold after <see cref="Groove"/> has moved notes around.
    /// </summary>
    public MonoOptions? Mono { get; set; }

    /// <summary>Moves the whole song back to leave silent bars in front of it; <c>null</c> starts at bar 1.</summary>
    public CountInOptions? CountIn { get; set; }
}

/// <summary>
/// Silent bars in front of the song, so there is a lead-in when playing the parts into hardware or recording
/// along. Everything moves: notes, chords, sections, meter and key changes, and the audio of a Logic project.
/// </summary>
public sealed record CountInOptions
{
    /// <summary>Number of bars, counted in the meter the song starts in.</summary>
    public int Bars { get; set; } = 1;

    /// <summary>A click on every beat of the lead-in, on the drum track; without it the bars stay silent.</summary>
    public bool Click { get; set; } = true;

    /// <summary>Note of the click; the General MIDI side stick by default, which every drum kit has.</summary>
    public int Note { get; set; } = GeneralMidiDrums.SideStick;

    public int Velocity { get; set; } = 100;

    /// <summary>The first beat of each lead-in bar is played harder, so the count is audible as a count.</summary>
    public int AccentVelocity { get; set; } = 120;
}

/// <summary>
/// A sustained pad of the notes that carry a chord's colour: its third and its seventh, or its fifth when the
/// chord has no seventh. Neighbouring chords that share both notes are held as one note instead of being re-struck.
/// </summary>
public sealed record GuideToneOptions
{
    /// <summary>Octaves relative to the default register, whose lower voice starts at MIDI 52 (E3, E2 in Logic).</summary>
    public int OctaveShift { get; set; }

    public int Velocity { get; set; } = 64;
}

/// <summary>Copies a voice of the score to a second track, transposed - by default the vocal an octave down.</summary>
public sealed record DoublingOptions
{
    /// <summary>Id of the voice to copy (<c>Vocal</c>, <c>Ins</c>; case-insensitive).</summary>
    public string VoiceId { get; set; } = "Vocal";

    /// <summary>Interval in semitones; -12 is the octave below.</summary>
    public int Semitones { get; set; } = -12;

    /// <summary>Velocity of the copy, so it can sit under the original; <c>null</c> keeps the original's.</summary>
    public int? Velocity { get; set; } = 80;
}

/// <summary>
/// Swing and humanization. Swing delays the off-beat subdivisions and shortens them by the same amount, so the
/// following note keeps its place. Humanization moves every note a little and varies its velocity.
/// </summary>
public sealed record GrooveOptions
{
    /// <summary>0 = straight, 1 = a full triplet feel; values in between are the usual light swing.</summary>
    public double Swing { get; set; }

    /// <summary>Which subdivision is swung.</summary>
    public SwingUnit SwingUnit { get; set; } = SwingUnit.Eighths;

    /// <summary>Largest timing deviation in milliseconds; 0 keeps every note exactly on the grid.</summary>
    public double HumanizeTimingMs { get; set; }

    /// <summary>Largest velocity deviation; 0 keeps the velocities as generated.</summary>
    public int HumanizeVelocity { get; set; }

    /// <summary>
    /// The velocity humanization starts from for a note that carries none of its own, which is every note read
    /// from the score. It matches the renderer's default for melody tracks.
    /// </summary>
    public int BaseVelocity { get; set; } = 96;

    /// <summary>Seed of the humanization, so the same score and options give the same result.</summary>
    public int Seed { get; set; } = 1;

    /// <summary>Swing the drum track along with the rest; off, the drums stay straight under a swung melody.</summary>
    public bool IncludeDrums { get; set; } = true;
}

public enum SwingUnit
{
    /// <summary>The second eighth of every beat is delayed.</summary>
    Eighths,

    /// <summary>The second and fourth sixteenth of every beat are delayed.</summary>
    Sixteenths,
}

/// <summary>
/// Prepares the melodic tracks for monophonic synthesizers: never more than one note at a time, a short gap
/// between consecutive notes so the envelope is re-triggered, and a minimum length for every note.
/// </summary>
public sealed record MonoOptions
{
    /// <summary>Gap in milliseconds a note leaves before the next one starts.</summary>
    public double GapMs { get; set; } = 12;

    /// <summary>Shortest note in milliseconds; shorter notes are stretched if there is room.</summary>
    public double MinimumLengthMs { get; set; } = 40;

    /// <summary>
    /// Stretches every note up to the start of the next one, so the track becomes a continuous line of gates.
    /// The gap is still kept, so the envelope re-triggers on every note.
    /// </summary>
    public bool Legato { get; set; }

    /// <summary>Also applies to the generated bass track, which is monophonic on most synthesizers too.</summary>
    public bool IncludeBass { get; set; } = true;
}

public sealed record ChordOptions
{
    public ChordPattern Pattern { get; set; } = ChordPattern.Block;

    /// <summary>Which inversion the chord is played in; by default every chord stands on its root.</summary>
    public ChordInversion Inversion { get; set; } = ChordInversion.RootPosition;

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

    /// <summary>The notes of the chord in eighths, up and back down without repeating the turning points.</summary>
    ArpeggioUpDown,

    /// <summary>The whole chord on every sixteenth note, a bed for fast passages.</summary>
    Sixteenths,
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

    /// <summary>
    /// Quarter notes walking from the chord's bass note over its third and fifth to the next chord, a step
    /// away from its bass note - the jazz and blues way of joining two chords.
    /// </summary>
    Walking,
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

    /// <summary>Kick on 1 and 3, snare on 2 and 4, hi-hat in sixteenths - the busier pop groove.</summary>
    SixteenthHats,

    /// <summary>
    /// Kick on 1 and 3, snare on 2 and 4, hi-hat on the first and third eighth-note triplet of every beat:
    /// the shuffle, which swings whatever the groove settings say.
    /// </summary>
    Shuffle,
}

/// <summary>Note numbers of the General MIDI drum map, which Logic's drum kits follow.</summary>
public static class GeneralMidiDrums
{
    public const int Channel = 9;
    public const int Kick = 36;
    public const int Snare = 38;
    public const int SideStick = 37;
    public const int ClosedHiHat = 42;
    public const int OpenHiHat = 46;
    public const int Crash = 49;
}
