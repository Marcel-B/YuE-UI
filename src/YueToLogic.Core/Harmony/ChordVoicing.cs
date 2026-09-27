using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <summary>The inversion a chord is voiced in. A slash bass stays below the voicing in every case.</summary>
public enum ChordInversion
{
    /// <summary>Every chord stands on its root, as written.</summary>
    RootPosition,

    /// <summary>
    /// The inversion closest to the chord before it, so the voices move as little as possible. This is the usual
    /// choice for an accompaniment: the chord track stops jumping an octave at every change.
    /// </summary>
    Closest,

    /// <summary>The third is the lowest note.</summary>
    First,

    /// <summary>The fifth is the lowest note.</summary>
    Second,
}

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
    public static IReadOnlyList<int> GetNotes(ChordSymbol chord, int rootOctaveBase = DefaultRootOctaveBase) =>
        GetNotes(chord, rootOctaveBase, ChordInversion.RootPosition);

    /// <summary>
    /// Returns the ascending MIDI notes of the chord in the requested inversion. Every voicing keeps its lowest
    /// chord tone inside the octave above <paramref name="rootOctaveBase"/>, so a chord track cannot drift out of
    /// its register however long the song is. A slash bass is added one octave below that.
    /// </summary>
    /// <param name="previous">
    /// The chord tones of the chord before it, without its slash bass. <see cref="ChordInversion.Closest"/> picks
    /// the inversion nearest to them; without it, and for every other mode, the inversion is fixed.
    /// </param>
    public static IReadOnlyList<int> GetNotes(
        ChordSymbol chord,
        int rootOctaveBase,
        ChordInversion inversion,
        IReadOnlyList<int>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var root = rootOctaveBase + chord.RootPitchClass;
        var rootPosition = GetIntervals(chord.Quality).Select(interval => root + interval).ToList();

        var notes = inversion switch
        {
            ChordInversion.First => Invert(rootPosition, 1, rootOctaveBase),
            ChordInversion.Second => Invert(rootPosition, 2, rootOctaveBase),
            ChordInversion.Closest when previous is { Count: > 0 } => Closest(rootPosition, rootOctaveBase, previous),
            _ => Invert(rootPosition, 0, rootOctaveBase),
        };

        if (chord.BassPitchClass is { } bass)
        {
            notes = [rootOctaveBase - 12 + bass, .. notes];
        }

        return notes;
    }

    /// <summary>
    /// Moves the lowest <paramref name="steps"/> chord tones up an octave and places the result so that its
    /// lowest note lies in the octave above <paramref name="rootOctaveBase"/>.
    /// </summary>
    private static List<int> Invert(IReadOnlyList<int> rootPosition, int steps, int rootOctaveBase)
    {
        var notes = rootPosition.ToList();
        for (var i = 0; i < Math.Min(steps, notes.Count - 1); i++)
        {
            notes[i] += 12;
        }

        notes.Sort();
        var octaves = (int)Math.Floor((notes[0] - rootOctaveBase) / 12.0);
        return octaves == 0 ? notes : [.. notes.Select(n => n - (octaves * 12))];
    }

    /// <summary>The inversion whose notes lie nearest to the previous chord's, measured by their average pitch.</summary>
    private static List<int> Closest(IReadOnlyList<int> rootPosition, int rootOctaveBase, IReadOnlyList<int> previous)
    {
        var target = previous.Average();
        List<int>? best = null;
        var bestDistance = double.MaxValue;
        for (var steps = 0; steps < rootPosition.Count; steps++)
        {
            var candidate = Invert(rootPosition, steps, rootOctaveBase);
            var distance = Math.Abs(candidate.Average() - target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best!;
    }
}
