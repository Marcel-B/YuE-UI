using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Moves the whole song back by a number of bars and fills them with a click, so a player or a recorder has a
/// lead-in. This is the one arrangement step that touches more than the tracks: chords, sections and every
/// meter and key change move with the music, or the song would come apart.
/// </summary>
internal static class CountInBuilder
{
    public const string ClickTrackId = "Drums";

    public static ScoreDocument Apply(ScoreDocument score, CountInOptions options)
    {
        var bars = Math.Max(0, options.Bars);
        if (bars == 0)
        {
            return score;
        }

        var signature = score.TimeSignatures.Count > 0
            ? score.TimeSignatures[0]
            : new TimeSignatureChange(0, 4, 4);
        var beatTicks = 4L * score.TicksPerQuarterNote / signature.Denominator;
        var offset = beatTicks * signature.Numerator * bars;

        // The signature and key the song opens in also govern the lead-in, so those two stay at tick 0 and
        // only the changes that follow move.
        var timeSignatures = Shift(score.TimeSignatures, offset, s => s.StartTicks, (s, t) => s with { StartTicks = t });
        var keySignatures = Shift(score.KeySignatures, offset, k => k.StartTicks, (k, t) => k with { StartTicks = t });

        var voices = score.Voices
            .Select(voice => voice with
            {
                Notes = [.. voice.Notes.Select(note => note with { StartTicks = note.StartTicks + offset })],
            })
            .ToList();

        if (options.Click)
        {
            AddClick(voices, options, beatTicks, signature.Numerator, bars);
        }

        return score with
        {
            TimeSignatures = timeSignatures,
            KeySignatures = keySignatures,
            Sections = [.. score.Sections.Select(s => s with { StartTicks = s.StartTicks + offset })],
            Chords = [.. score.Chords.Select(c => c with { StartTicks = c.StartTicks + offset })],
            Voices = voices,
            LengthTicks = score.LengthTicks + offset,
            CountInTicks = score.CountInTicks + offset,
        };
    }

    /// <summary>Everything after the first entry moves; the first one describes the lead-in as well.</summary>
    private static List<T> Shift<T>(IReadOnlyList<T> items, long offset, Func<T, long> start, Func<T, long, T> move) =>
        [.. items.Select((item, index) => index == 0 ? item : move(item, start(item) + offset))];

    /// <summary>
    /// The click goes on the drum track, which is where a drum kit is already listening; a score without drums
    /// gets one for it. Its notes sit in front of the music, which has been moved out of the way.
    /// </summary>
    private static void AddClick(List<VoiceTrack> voices, CountInOptions options, long beatTicks, int beatsPerBar, int bars)
    {
        var note = Math.Clamp(options.Note, 0, 127);
        var accent = Math.Clamp(options.AccentVelocity, 1, 127);
        var plain = Math.Clamp(options.Velocity, 1, 127);
        var clicks = Enumerable.Range(0, beatsPerBar * bars).Select(beat => new NoteEvent(
            beat * beatTicks,
            Math.Max(1, beatTicks / 4),
            note,
            // The downbeat of each lead-in bar is played harder, so the count is audible as a count.
            beat % beatsPerBar == 0 ? accent : plain));

        var index = voices.FindIndex(voice => voice.Kind == TrackKind.Drums);
        if (index < 0)
        {
            voices.Add(new VoiceTrack(ClickTrackId, ClickTrackId, [.. clicks], TrackKind.Drums));
            return;
        }

        voices[index] = voices[index] with { Notes = [.. clicks, .. voices[index].Notes] };
    }
}
