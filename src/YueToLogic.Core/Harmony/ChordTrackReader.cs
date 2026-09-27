using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <param name="Start">Unit position of the beat the chord begins on.</param>
internal readonly record struct ChordChange(long Start, ChordSymbol Symbol);

/// <param name="Unrecognized">Beats whose notes formed no chord of the vocabulary; they keep the chord before them.</param>
/// <param name="Incomplete">Chords named from fewer notes than they have, e.g. a bare fifth read as a major chord.</param>
internal sealed record ChordTrackReading(IReadOnlyList<ChordChange> Changes, int Unrecognized, int Incomplete);

/// <summary>
/// Turns the notes of a chord track back into chord symbols, beat by beat: whatever pattern
/// <see cref="Arrangement.ChordTrackGenerator"/> played them in, or a player in Logic did.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Several notes struck together that form a complete chord are that chord: block chords, the eighth and
/// sixteenth pulses and the off-beat stabs all strike the whole chord.</item>
/// <item>Notes that only continue the chord before them, held or picked from it, change nothing.</item>
/// <item>Anything else is gathered over up to <see cref="MaxGatheredBeats"/> beats until it forms a complete chord,
/// which is how an arpeggio, playing one note per step, is read.</item>
/// </list>
/// A chord starts on the beat its notes begin in; YuE2 scores change chords on bar lines and beats, and a stab
/// played after the beat still belongs to it. An arpeggio that opens a bar with tones the chord before it shares
/// (Dm to Bb, starting on D and F) only shows the change a beat or two later; when every note since the bar line
/// fits the new chord, the change goes back to the bar line. Silence keeps the chord before it, as a chord
/// symbol does.
/// </remarks>
internal static class ChordTrackReader
{
    private const int MaxGatheredBeats = 4;

    /// <summary>More different notes than any chord of the vocabulary with a slash bass has: the beats span a change.</summary>
    private const int MaxPitchClasses = 5;

    public static ChordTrackReading Read(IReadOnlyList<GridNote> notes, BarGrid grid)
    {
        var beats = grid.Bars
            .SelectMany(bar => Enumerable.Range(0, (int)((bar.Length + bar.Beat - 1) / bar.Beat))
                .Select(i => (Start: bar.Start + (i * bar.Beat), End: Math.Min(bar.End, bar.Start + ((i + 1) * bar.Beat)), Bar: bar.Start)))
            .ToList();
        var changes = new List<ChordChange>();
        HashSet<int> current = [];
        var unrecognized = 0;
        var incomplete = 0;

        var index = 0;
        while (index < beats.Count)
        {
            var (start, end, barStart) = beats[index];
            var sounding = Sounding(notes, start, end);
            if (sounding.Count == 0)
            {
                index++;
                continue;
            }

            var struck = sounding
                .Where(n => n.Start >= start)
                .GroupBy(n => n.Start)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .FirstOrDefault();
            if (struck is not null && struck.Count() >= 2 && ChordRecognizer.Recognize(struck.Select(n => n.Pitch)) is { Complete: true } chord)
            {
                Change(start, chord.Symbol);
                index++;
                continue;
            }

            if (changes.Count > 0 && sounding.All(n => current.Contains(PitchClass(n.Pitch))))
            {
                index++;
                continue;
            }

            var gathered = sounding.Select(n => n.Pitch).ToList();
            var match = ChordRecognizer.Recognize(gathered);
            var last = index;
            while (match is not { Complete: true } && last + 1 < beats.Count && last - index < MaxGatheredBeats - 1)
            {
                var next = Sounding(notes, beats[last + 1].Start, beats[last + 1].End);
                var wider = gathered.Concat(next.Select(n => n.Pitch)).ToList();
                var widerMatch = next.Count == 0 || wider.Select(PitchClass).Distinct().Count() > MaxPitchClasses
                    ? null
                    : ChordRecognizer.Recognize(wider);
                if (widerMatch is null || (match is not null && widerMatch.Score < match.Score))
                {
                    break;
                }

                (gathered, match, last) = (wider, widerMatch, last + 1);
            }

            if (match is null)
            {
                unrecognized++;
            }
            else
            {
                incomplete += match.Complete ? 0 : 1;
                var tones = Tones(match.Symbol);
                var opensBar = start > barStart
                    && (changes.Count == 0 || changes[^1].Start < barStart)
                    && Sounding(notes, barStart, start).All(n => tones.Contains(PitchClass(n.Pitch)));
                Change(opensBar ? barStart : start, match.Symbol);
            }

            index = last + 1;
        }

        return new ChordTrackReading(changes, unrecognized, incomplete);

        void Change(long start, ChordSymbol symbol)
        {
            if (changes.Count == 0 || changes[^1].Symbol != symbol)
            {
                changes.Add(new ChordChange(start, symbol));
            }

            current = Tones(symbol);
        }
    }

    /// <summary>The pitch classes a chord sounds, its slash bass included.</summary>
    private static HashSet<int> Tones(ChordSymbol symbol)
    {
        var tones = ChordVoicing.GetIntervals(symbol.Quality).Select(i => (symbol.RootPitchClass + i) % 12).ToHashSet();
        if (symbol.BassPitchClass is { } bass)
        {
            tones.Add(bass);
        }

        return tones;
    }

    private static List<GridNote> Sounding(IReadOnlyList<GridNote> notes, long start, long end) =>
        notes.Where(n => n.Start < end && n.End > start).ToList();

    private static int PitchClass(int pitch) => ((pitch % 12) + 12) % 12;
}
