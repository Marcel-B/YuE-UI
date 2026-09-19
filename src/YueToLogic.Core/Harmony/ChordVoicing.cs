using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <summary>Turns a chord symbol into concrete MIDI notes: a close block voicing with the root in octave 3.</summary>
public static class ChordVoicing
{
    /// <summary>MIDI note of C3, the octave the chord root is placed in.</summary>
    public const int DefaultRootOctaveBase = 48;

    public static IReadOnlyList<int> GetIntervals(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => [0, 4, 7],
        ChordQuality.Minor => [0, 3, 7],
        ChordQuality.Diminished => [0, 3, 6],
        ChordQuality.Augmented => [0, 4, 8],
        ChordQuality.Dominant7 => [0, 4, 7, 10],
        ChordQuality.Major7 => [0, 4, 7, 11],
        ChordQuality.Minor7 => [0, 3, 7, 10],
        ChordQuality.Diminished7 => [0, 3, 6, 9],
        ChordQuality.HalfDiminished7 => [0, 3, 6, 10],
        ChordQuality.Suspended4 => [0, 5, 7],
        ChordQuality.Suspended2 => [0, 2, 7],
        ChordQuality.Major6 => [0, 4, 7, 9],
        ChordQuality.Minor6 => [0, 3, 7, 9],
        ChordQuality.Dominant7Suspended4 => [0, 5, 7, 10],
        ChordQuality.MinorMajor7 => [0, 3, 7, 11],
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
    };

    /// <summary>
    /// Returns the ascending MIDI notes of the chord. A slash bass is added one octave below the root octave.
    /// </summary>
    public static IReadOnlyList<int> GetNotes(ChordSymbol chord, int rootOctaveBase = DefaultRootOctaveBase)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var root = rootOctaveBase + chord.RootPitchClass;
        var notes = GetIntervals(chord.Quality).Select(interval => root + interval).ToList();
        if (chord.BassPitchClass is { } bass)
        {
            notes.Insert(0, rootOctaveBase - 12 + bass);
        }

        return notes;
    }
}
