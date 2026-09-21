using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Gives the finished tracks a groove: swing delays the off-beat subdivisions, humanization moves every note a
/// little and varies its velocity. Both work on the notes themselves, so the MIDI file, the JSON dump and the
/// Logic project all carry the same timing - nothing here depends on a host or a plug-in.
/// </summary>
internal static class GrooveProcessor
{
    public static IReadOnlyList<VoiceTrack> Apply(ScoreDocument score, IReadOnlyList<VoiceTrack> tracks, GrooveOptions options)
    {
        var unit = options.SwingUnit == SwingUnit.Sixteenths
            ? Math.Max(1L, score.TicksPerQuarterNote / 4)
            : Math.Max(1L, score.TicksPerQuarterNote / 2);

        // A full triplet feel moves the off-beat note a third of its unit later.
        var swingTicks = (long)Math.Round(Math.Clamp(options.Swing, 0, 1) * unit / 3.0);
        var jitterTicks = (long)Math.Round(options.HumanizeTimingMs / 1000.0 * score.TempoBpm / 60.0 * score.TicksPerQuarterNote);
        var velocityAmount = Math.Max(0, options.HumanizeVelocity);
        if (swingTicks == 0 && jitterTicks == 0 && velocityAmount == 0)
        {
            return tracks;
        }

        var result = new List<VoiceTrack>(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];

            // Seeded per track, so the same score and options always produce the same file.
            var random = new Random(options.Seed + i);
            var swung = track.Kind == TrackKind.Drums && !options.IncludeDrums ? 0 : swingTicks;
            var notes = track.Notes
                .Select(note => Groove(note, unit, swung, jitterTicks, velocityAmount, options.BaseVelocity, random))
                .OrderBy(note => note.StartTicks)
                .ThenBy(note => note.NoteNumber)
                .ToArray();
            result.Add(track with { Notes = notes });
        }

        return result;
    }

    private static NoteEvent Groove(
        NoteEvent note,
        long unit,
        long swingTicks,
        long jitterTicks,
        int velocityAmount,
        int baseVelocity,
        Random random)
    {
        var start = note.StartTicks;
        var duration = note.DurationTicks;

        // Only notes that sit exactly on an off-beat subdivision are swung; anything else keeps its place,
        // so an already syncopated melody is not pushed around twice.
        if (swingTicks > 0 && start % (2 * unit) == unit)
        {
            start += swingTicks;
            duration = Math.Max(1, duration - swingTicks);
        }

        if (jitterTicks > 0)
        {
            start = Math.Max(0, start + (long)Math.Round(Deviation(random) * jitterTicks));
        }

        var velocity = note.Velocity;
        if (velocityAmount > 0)
        {
            velocity = Math.Clamp((velocity ?? baseVelocity) + (int)Math.Round(Deviation(random) * velocityAmount), 1, 127);
        }

        return note with { StartTicks = start, DurationTicks = duration, Velocity = velocity };
    }

    /// <summary>A value in [-1, 1], weighted towards the middle so that extremes stay rare.</summary>
    private static double Deviation(Random random) => (random.NextDouble() + random.NextDouble()) - 1.0;
}
