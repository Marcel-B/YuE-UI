using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Makes a melodic track playable by a monophonic synthesizer. Three things get in the way of one: two notes
/// sounding at once, which a mono synth answers by dropping one of them; notes that touch or overlap, where the
/// envelope is never re-triggered and two notes come out as one long slide; and notes so short that a slow
/// envelope never opens. This removes all three from the note material itself, so the result behaves the same
/// through any MIDI interface and in any host.
/// </summary>
internal static class MonoProcessor
{
    public static VoiceTrack Apply(ScoreDocument score, VoiceTrack track, MonoOptions options)
    {
        var gap = ToTicks(score, options.GapMs);
        var minimum = ToTicks(score, options.MinimumLengthMs);
        var notes = Monophonic(track.Notes);
        var result = new List<NoteEvent>(notes.Count);

        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            var end = note.StartTicks + note.DurationTicks;
            var hasNext = i + 1 < notes.Count;

            // The latest this note may end: the gap keeps the next note's attack audible as a new attack.
            var limit = hasNext ? notes[i + 1].StartTicks - gap : long.MaxValue;

            if (options.Legato && hasNext)
            {
                end = Math.Max(end, limit);
            }

            if (end - note.StartTicks < minimum)
            {
                end = note.StartTicks + minimum;
            }

            end = Math.Min(end, limit);
            result.Add(note with { DurationTicks = Math.Max(1, end - note.StartTicks) });
        }

        return track with { Notes = result };
    }

    /// <summary>
    /// One note at a time: of two notes starting together the higher one is kept, which is the melody in
    /// practice; a note that starts while another still sounds cuts the earlier one short, as a mono synth does.
    /// </summary>
    private static List<NoteEvent> Monophonic(IReadOnlyList<NoteEvent> notes)
    {
        var sorted = notes
            .OrderBy(note => note.StartTicks)
            .ThenByDescending(note => note.NoteNumber)
            .ToList();
        var result = new List<NoteEvent>(sorted.Count);

        foreach (var note in sorted)
        {
            if (result.Count > 0 && result[^1].StartTicks == note.StartTicks)
            {
                // Sorted by descending pitch, so the note already kept is the higher one.
                continue;
            }

            if (result.Count > 0)
            {
                var previous = result[^1];
                var overlap = previous.StartTicks + previous.DurationTicks - note.StartTicks;
                if (overlap > 0)
                {
                    result[^1] = previous with { DurationTicks = Math.Max(1, previous.DurationTicks - overlap) };
                }
            }

            result.Add(note);
        }

        return result;
    }

    private static long ToTicks(ScoreDocument score, double milliseconds) =>
        Math.Max(0, (long)Math.Round(milliseconds / 1000.0 * score.TempoBpm / 60.0 * score.TicksPerQuarterNote));
}
