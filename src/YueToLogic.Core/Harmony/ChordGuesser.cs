using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <summary>
/// Chords for a melody that comes without any: per half bar (a whole bar in meters that do not split evenly into
/// two), the triad of the key whose notes cover most of what the melody sings there. Ties go to the chords a pop
/// song leans on most (I, V, IV, vi) and to keeping the chord before; a window without notes keeps it too.
/// </summary>
internal static class ChordGuesser
{
    /// <summary>How much a sung note outside the chord counts against it, relative to a chord tone counting for it.</summary>
    private const double OutsidePenalty = 0.6;

    /// <summary>Weight of a note starting a window: the note on the strong beat says most about the harmony.</summary>
    private const double DownbeatBonus = 0.5;

    /// <summary>
    /// Bonus for keeping the chord before, as a share of the window's weight: a melody note that fits both the
    /// chord before and the tonic (the c after F and A) keeps the chord rather than jumping home.
    /// </summary>
    private const double Continuity = 0.15;

    public static IReadOnlyList<ChordEvent> Guess(ScoreDocument score, IReadOnlyList<NoteEvent> melody, ScaleKey key)
    {
        var candidates = Candidates(key);
        var chords = new List<ChordEvent>();
        Candidate? previous = null;
        var sharpNames = key.Sharps >= 0;
        foreach (var (start, end) in Windows(score))
        {
            var chosen = previous;
            var best = double.NegativeInfinity;
            var total = 0.0;
            var weights = new double[12];
            foreach (var note in melody)
            {
                var overlap = Math.Min(end, note.StartTicks + note.DurationTicks) - Math.Max(start, note.StartTicks);
                if (overlap > 0)
                {
                    var weight = overlap * (note.StartTicks == start ? 1 + DownbeatBonus : 1);
                    weights[ScaleKey.Mod12(note.NoteNumber)] += weight;
                    total += weight;
                }
            }

            if (total > 0)
            {
                foreach (var candidate in candidates)
                {
                    var fit = 0.0;
                    for (var pitchClass = 0; pitchClass < 12; pitchClass++)
                    {
                        fit += weights[pitchClass] * (candidate.PitchClasses.Contains(pitchClass) ? 1 : -OutsidePenalty);
                    }

                    fit += total * (candidate.Preference + (candidate == previous ? Continuity : 0));
                    if (fit > best)
                    {
                        best = fit;
                        chosen = candidate;
                    }
                }
            }

            chosen ??= candidates[0];
            if (chords.Count > 0 && previous == chosen && chords[^1].StartTicks + chords[^1].DurationTicks == start)
            {
                chords[^1] = chords[^1] with { DurationTicks = end - chords[^1].StartTicks };
            }
            else
            {
                var symbol = new ChordSymbol(chosen.Root, chosen.Quality, null);
                chords.Add(new ChordEvent(start, end - start, Name(symbol, sharpNames), symbol));
            }

            previous = chosen;
        }

        return chords;
    }

    /// <summary>The harmonic windows of the score: half bars in even meters of four or more beats, whole bars otherwise.</summary>
    private static IEnumerable<(long Start, long End)> Windows(ScoreDocument score)
    {
        var signatures = score.TimeSignatures.Count > 0 ? score.TimeSignatures : [new TimeSignatureChange(0, 4, 4)];
        var position = signatures[0].StartTicks;
        for (var i = 0; i < signatures.Count && position < score.LengthTicks; i++)
        {
            var signature = signatures[i];
            var segmentEnd = i + 1 < signatures.Count ? signatures[i + 1].StartTicks : score.LengthTicks;
            var bar = 4L * score.TicksPerQuarterNote * signature.Numerator / signature.Denominator;
            var split = signature.Numerator >= 4 && signature.Numerator % 2 == 0 && signature.Denominator == 4;
            var window = split ? bar / 2 : bar;
            for (position = Math.Max(position, signature.StartTicks); position < segmentEnd && window > 0; position += window)
            {
                yield return (position, Math.Min(position + window, score.LengthTicks));
            }
        }
    }

    /// <summary>The triads on every degree of the scale; in minor also the major dominant the raised leading tone makes.</summary>
    private static List<Candidate> Candidates(ScaleKey key)
    {
        // Preference by degree above the tonic, as a share of the melody's weight in the window.
        var preferences = key.IsMinor
            ? new Dictionary<int, double> { [0] = 0.3, [5] = 0.2, [8] = 0.2, [3] = 0.15, [10] = 0.15, [7] = 0.1, [2] = -0.2 }
            : new Dictionary<int, double> { [0] = 0.3, [7] = 0.25, [5] = 0.2, [9] = 0.15, [2] = 0.1, [4] = 0.05, [11] = -0.2 };
        var candidates = new List<Candidate>();
        foreach (var (degree, preference) in preferences)
        {
            var root = (key.TonicPitchClass + degree) % 12;
            var third = key.Step(root + 12, 2) - (root + 12);
            var fifth = key.Step(root + 12, 4) - (root + 12);
            var quality = (third, fifth) switch
            {
                (4, 7) => ChordQuality.Major,
                (3, 7) => ChordQuality.Minor,
                (3, 6) => ChordQuality.Diminished,
                _ => ChordQuality.Augmented,
            };
            candidates.Add(new Candidate(root, quality, preference));
        }

        if (key.IsMinor)
        {
            candidates.Add(new Candidate((key.TonicPitchClass + 7) % 12, ChordQuality.Major, 0.12));
        }

        // The tonic first: it is what an empty start falls back to.
        return [.. candidates.OrderByDescending(c => c.Preference)];
    }

    private static string Name(ChordSymbol symbol, bool sharps)
    {
        string[] names = sharps
            ? ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
            : ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
        return names[symbol.RootPitchClass] + symbol.Quality switch
        {
            ChordQuality.Minor => "m",
            ChordQuality.Diminished => "dim",
            ChordQuality.Augmented => "aug",
            _ => string.Empty,
        };
    }

    private sealed record Candidate(int Root, ChordQuality Quality, double Preference)
    {
        public HashSet<int> PitchClasses { get; } = [.. ChordVoicing.GetIntervals(Quality).Select(i => (Root + i) % 12)];
    }
}
