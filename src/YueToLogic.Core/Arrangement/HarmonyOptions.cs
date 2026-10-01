namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Backing vocals derived from a melody: parts that sing the melody's rhythm (the same syllables) in other
/// registers, in the key of the song and, where the score has chord symbols, on the chord. Each part becomes a
/// track of its own. <c>set</c> rather than <c>init</c> for the same reason as <see cref="Conversion.ConversionOptions"/>.
/// </summary>
public sealed record HarmonyOptions
{
    /// <summary>The parts to add, each once; an empty list adds none.</summary>
    public IReadOnlyList<HarmonyPart> Parts { get; set; } = [HarmonyPart.ThirdAbove];

    /// <summary>Id of the voice the parts are derived from (<c>Vocal</c>, <c>Ins</c>; case-insensitive).</summary>
    public string VoiceId { get; set; } = "Vocal";

    /// <summary>
    /// The key the parts stay in, as a <c>K:</c> field writes it (<c>D</c>, <c>F#m</c>, <c>Bb</c>). <c>null</c>
    /// takes the key signature of the score where the melody fits it and otherwise the key the melody suggests,
    /// since a MIDI file without a key signature reads as C major.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Only notes in sections whose name contains one of these (case-insensitive) get backing vocals, e.g.
    /// <c>chorus</c>; empty for the whole song.
    /// </summary>
    public IReadOnlyList<string> Sections { get; set; } = [];

    /// <summary>Velocity of the parts, so they sit under the lead.</summary>
    public int Velocity { get; set; } = 80;
}

/// <summary>A backing part. The parallel ones follow the melody at a fixed number of scale steps; the choir parts
/// fill the chord below it as in a four-part setting, with the melody as the soprano.</summary>
public enum HarmonyPart
{
    /// <summary>A third above in the scale (major or minor as the key has it), moving to a chord tone where the melody sits on one.</summary>
    ThirdAbove,

    /// <summary>A third below in the scale, on the chord likewise.</summary>
    ThirdBelow,

    /// <summary>A sixth below in the scale: the third above, an octave lower, which suits a lower voice.</summary>
    SixthBelow,

    /// <summary>Alto of a four-part setting: the chord tone just below the melody.</summary>
    Alto,

    /// <summary>Tenor of a four-part setting, below the alto.</summary>
    Tenor,

    /// <summary>Bass of a four-part setting: the chord's bass note, below the tenor.</summary>
    Bass,

    /// <summary>
    /// A drone (Bordun): the key's tonic under each phrase of the melody, one pitch per phrase, sung in the melody's
    /// rhythm like the other parts.
    /// </summary>
    Drone,

    /// <summary>The same tonic held from the start of each phrase to its end, the "ooh" a choir holds under the lead.</summary>
    DroneHeld,
}
