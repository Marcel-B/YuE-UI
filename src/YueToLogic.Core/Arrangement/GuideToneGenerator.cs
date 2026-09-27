using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Plays the two notes that tell a chord apart from its neighbours: its third and its seventh, or its fifth when
/// the chord has none. One sustained pair per chord, held across chords that share both notes, which gives the
/// long lines a pad or a string section plays while bass and drums carry the rhythm.
/// </summary>
internal static class GuideToneGenerator
{
    public const string TrackId = "Guide";

    /// <summary>MIDI 52 (E3; E2 in Logic's naming): both notes are placed in the octave above it.</summary>
    private const int LowestNote = 52;

    /// <summary>The colour note: the third, or the second or fourth of a suspended chord.</summary>
    private static readonly int[] ThirdIntervals = [3, 4, 2, 5];

    /// <summary>The seventh, or the sixth of a sixth chord; the diminished seventh is 9 as well.</summary>
    private static readonly int[] SeventhIntervals = [10, 11, 9];

    /// <summary>The fallback for a triad, which has no seventh to lean on.</summary>
    private static readonly int[] FifthIntervals = [7, 6, 8];

    public static VoiceTrack Generate(ScoreDocument score, GuideToneOptions options)
    {
        var lowest = LowestNote + (12 * options.OctaveShift);
        var notes = new List<NoteEvent>();

        // A held pair is written once the following chord no longer shares it.
        int[]? heldPitches = null;
        var heldStart = 0L;
        var heldEnd = 0L;

        foreach (var chord in score.Chords)
        {
            if (chord.Symbol is not { } symbol || chord.DurationTicks <= 0)
            {
                continue;
            }

            var pitches = Pitches(symbol, lowest);
            if (heldPitches is not null && heldEnd == chord.StartTicks && heldPitches.AsSpan().SequenceEqual(pitches))
            {
                heldEnd = chord.StartTicks + chord.DurationTicks;
                continue;
            }

            Flush(notes, heldPitches, heldStart, heldEnd, options.Velocity);
            heldPitches = pitches;
            heldStart = chord.StartTicks;
            heldEnd = chord.StartTicks + chord.DurationTicks;
        }

        Flush(notes, heldPitches, heldStart, heldEnd, options.Velocity);
        notes.Sort((a, b) => a.StartTicks != b.StartTicks ? a.StartTicks.CompareTo(b.StartTicks) : a.NoteNumber.CompareTo(b.NoteNumber));
        return new VoiceTrack(TrackId, TrackId, notes, TrackKind.GuideTones);
    }

    private static void Flush(List<NoteEvent> notes, int[]? pitches, long start, long end, int velocity)
    {
        if (pitches is null || end <= start)
        {
            return;
        }

        var clamped = Math.Clamp(velocity, 1, 127);
        notes.AddRange(pitches.Select(pitch => new NoteEvent(start, end - start, pitch, clamped)));
    }

    /// <summary>The two guide tones, each placed in the octave above <paramref name="lowest"/>, in ascending order.</summary>
    private static int[] Pitches(ChordSymbol symbol, int lowest)
    {
        var intervals = ChordVoicing.GetIntervals(symbol.Quality);
        var third = Pick(intervals, ThirdIntervals);
        var upper = Pick(intervals, SeventhIntervals) ?? Pick(intervals, FifthIntervals);
        var pitches = new[] { third, upper }
            .Where(interval => interval is not null)
            .Select(interval => InOctave(symbol.RootPitchClass + interval!.Value, lowest))
            .Distinct()
            .Order()
            .ToArray();

        return pitches;
    }

    private static int? Pick(IReadOnlyList<int> intervals, IReadOnlyList<int> wanted)
    {
        foreach (var candidate in wanted)
        {
            if (intervals.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static int InOctave(int pitchClass, int lowest) => lowest + (((pitchClass - lowest) % 12 + 12) % 12);
}
