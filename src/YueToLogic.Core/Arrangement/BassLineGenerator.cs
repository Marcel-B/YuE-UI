using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>Plays the bass note of every chord symbol on a fixed rhythmic grid.</summary>
internal static class BassLineGenerator
{
    public const string TrackId = "Bass";

    /// <summary>
    /// MIDI 40 (E2; E1 in Logic's naming): bass notes are placed in the octave above it. This is one octave
    /// above the lowest bass-guitar string, because many instruments cannot play the bottom octave.
    /// </summary>
    private const int LowestNote = 40;

    private const int OffBeatSoftening = 16;

    public static VoiceTrack Generate(ScoreDocument score, BassOptions options)
    {
        var ppq = score.TicksPerQuarterNote;
        var step = options.Pattern == BassPattern.Eighths ? Math.Max(1, ppq / 2) : ppq;
        var notes = new List<NoteEvent>();

        foreach (var chord in score.Chords)
        {
            if (chord.Symbol is not { } symbol)
            {
                continue;
            }

            // A slash chord (C/E) names the bass note explicitly.
            var bassNote = LowestNote + (12 * options.OctaveShift) + Mod12((symbol.BassPitchClass ?? symbol.RootPitchClass) - LowestNote);
            var alternateNote = AlternateNote(symbol, bassNote);
            var end = chord.StartTicks + chord.DurationTicks;
            var index = 0;

            // Steps stay on the absolute grid, so a chord starting off the beat does not shift the rhythm.
            for (var tick = chord.StartTicks; tick < end; index++)
            {
                var next = Math.Min(end, (tick / step + 1) * step);
                var pitch = options.Pattern == BassPattern.RootFifth && index % 2 == 1 ? alternateNote : bassNote;
                var velocity = tick % ppq == 0 ? options.Velocity : options.Velocity - OffBeatSoftening;

                // Slightly detached so that repeated notes are audible as separate attacks.
                var duration = Math.Max(1, (next - tick) * 9 / 10);
                notes.Add(new NoteEvent(tick, duration, pitch, Math.Clamp(velocity, 1, 127)));
                tick = next;
            }
        }

        return new VoiceTrack(TrackId, TrackId, notes, TrackKind.Bass);
    }

    /// <summary>The chord's fifth above the bass note; the root instead if the bass already is the fifth (C/G).</summary>
    private static int AlternateNote(ChordSymbol symbol, int bassNote)
    {
        var fifthPitchClass = symbol.RootPitchClass + ChordVoicing.GetIntervals(symbol.Quality)[2];
        var fifth = bassNote + Mod12(fifthPitchClass - bassNote);
        return fifth != bassNote ? fifth : bassNote + Mod12(symbol.RootPitchClass - bassNote);
    }

    private static int Mod12(int value) => ((value % 12) + 12) % 12;
}
