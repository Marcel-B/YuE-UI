using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <param name="Complete">Whether every tone of the chord sounds; an incomplete match is the best guess from fewer notes.</param>
internal sealed record ChordMatch(ChordSymbol Symbol, bool Complete, int Score);

/// <summary>
/// Names the chord a set of notes forms, within the closed vocabulary of the YuE2 dialect: the reverse of
/// <see cref="ChordVoicing"/>. Used to read the chord track of a MIDI file that came back from Logic.
/// </summary>
/// <remarks>
/// Every root and quality is scored by how many of its tones sound, how many are missing and how many notes it
/// leaves unexplained. The lowest note then decides between reading it as a tone of the chord and reading it as a
/// bass of its own under a chord complete above it (a slash chord):
/// <list type="bullet">
/// <item>On the root, the chord stands in root position: C E G A over C is C6, over A it is Am7.</item>
/// <item>On the third or fifth of a triad inside an octave, it is an inversion: E G C is C, the way the chord
/// track's inversions play it.</item>
/// <item>Otherwise, when a chord is complete above it, it is that chord's bass. That is how <see cref="ChordVoicing"/>
/// plays a slash chord: C/E is E2 below C3 E3 G3, Am/G is G2 below A3 C4 E4, C/B is B2 right below C3 E3 G3. It
/// also keeps the bass where a four-note chord in an inversion names the same notes: Bb C Eb G is Cm/Bb, not Eb6,
/// whose bass would be Eb.</item>
/// </list>
/// Of two slash readings with the same notes the one whose root is the lowest note above the bass wins, since the
/// voicing stacks the chord from its root: G Bb D E over Bb is Gm6/Bb rather than Em7b5/Bb. Some sets are
/// ambiguous even so: C E G A inside an octave is C6, also when it was Am7 in its first inversion; the same notes
/// sound either way. One or no matching tone is no chord at all.
/// </remarks>
internal static class ChordRecognizer
{
    private const int PerTone = 10;
    private const int PerMissingTone = 12;
    private const int PerExtraNote = 8;
    private const int RootInBass = 4;
    private const int SlashBass = 3;
    private const int RootAtBottom = 1;
    private const int DetachedBass = 15;
    private const int ColourInBass = 14;

    private static readonly ChordQuality[] Qualities = Enum.GetValues<ChordQuality>();

    /// <returns>The best reading, or <c>null</c> if the notes do not form a chord of the vocabulary at all.</returns>
    public static ChordMatch? Recognize(IEnumerable<int> pitches)
    {
        var notes = pitches.Distinct().Order().ToArray();
        if (notes.Length < 2)
        {
            return null;
        }

        var all = notes.Select(PitchClass).ToHashSet();
        var bass = PitchClass(notes[0]);
        var above = notes.Skip(1).Select(PitchClass).ToHashSet();
        var lowestAbove = PitchClass(notes[1]);
        var close = notes[^1] - notes[0] < 12;

        ChordMatch? plain = null;
        ChordMatch? slash = null;
        foreach (var quality in Qualities)
        {
            var intervals = ChordVoicing.GetIntervals(quality);
            for (var root = 0; root < 12; root++)
            {
                var tones = intervals.Select(i => (root + i) % 12).ToHashSet();
                Consider(ref plain, Plain(root, quality, tones, all, bass, close));
                if (bass != root)
                {
                    Consider(ref slash, Slash(root, quality, tones, above, bass, lowestAbove));
                }
            }
        }

        if (plain is null || slash is null)
        {
            return plain ?? slash;
        }

        // A clean slash reading (the chord complete above the bass, nothing left over) wins over an inversion,
        // except the plain inversion of a triad inside an octave.
        var inversion = bass != plain.Symbol.RootPitchClass;
        var triadInside = close && ChordVoicing.GetIntervals(plain.Symbol.Quality).Count == 3 && IsThirdOrFifth(bass, plain.Symbol.RootPitchClass);
        if (inversion && !triadInside && slash.Score >= CleanSlash(slash))
        {
            return slash;
        }

        return slash.Score > plain.Score ? slash : plain;
    }

    /// <summary>The chord with the lowest note as one of its own tones.</summary>
    private static ChordMatch? Plain(int root, ChordQuality quality, HashSet<int> tones, HashSet<int> sounding, int bass, bool close)
    {
        if (!sounding.Contains(root))
        {
            return null;
        }

        var matched = tones.Count(sounding.Contains);
        var missing = tones.Count - matched;
        var extra = sounding.Count(pc => !tones.Contains(pc));
        if (matched < 2)
        {
            return null;
        }

        var score = (PerTone * matched) - (PerMissingTone * missing) - (PerExtraNote * extra);
        if (bass == root)
        {
            score += RootInBass;
        }
        else if (tones.Contains(bass))
        {
            // A third or fifth inside the voicing makes an inversion; anything else at the bottom, or a note set
            // apart below the chord, sounds like a bass of its own.
            if (!IsThirdOrFifth(bass, root))
            {
                score -= ColourInBass;
            }

            if (!close)
            {
                score -= DetachedBass;
            }
        }

        return new ChordMatch(new ChordSymbol(root, quality, null), missing == 0, score);
    }

    /// <summary>The chord complete above the lowest note, which is a bass of its own.</summary>
    private static ChordMatch? Slash(int root, ChordQuality quality, HashSet<int> tones, HashSet<int> above, int bass, int lowestAbove)
    {
        if (!tones.IsSubsetOf(above))
        {
            return null;
        }

        var extra = above.Count(pc => !tones.Contains(pc));
        var score = (PerTone * tones.Count) - (PerExtraNote * extra) - SlashBass + (root == lowestAbove ? RootAtBottom : 0);
        return new ChordMatch(new ChordSymbol(root, quality, bass), true, score);
    }

    /// <summary>The lowest score a slash reading without notes left over can have.</summary>
    private static int CleanSlash(ChordMatch slash) =>
        (PerTone * ChordVoicing.GetIntervals(slash.Symbol.Quality).Count) - SlashBass;

    private static bool IsThirdOrFifth(int bass, int root) => ((bass - root + 12) % 12) is 3 or 4 or 6 or 7 or 8;

    /// <summary>A strictly better score wins, so of equal readings the simpler quality (listed first) stays.</summary>
    private static void Consider(ref ChordMatch? best, ChordMatch? candidate)
    {
        if (candidate is not null && (best is null || candidate.Score > best.Score))
        {
            best = candidate;
        }
    }

    private static int PitchClass(int pitch) => ((pitch % 12) + 12) % 12;
}
