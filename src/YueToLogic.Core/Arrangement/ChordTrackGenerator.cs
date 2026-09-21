using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Plays the chord symbols of the score: as written (<see cref="ChordPattern.Block"/>), as a repeated eighth-note
/// pulse, as short off-beat chords or as an arpeggio. The notes are generated once here so that the MIDI file and
/// the Logic project always play the same chord track.
/// </summary>
internal static class ChordTrackGenerator
{
    public const string TrackId = "Chords";

    private const int OffBeatSoftening = 10;

    /// <summary>Repeated chords are shortened so that each one is a separate attack.</summary>
    private const double Detached = 0.9;

    /// <summary>Off-beat chords are short stabs.</summary>
    private const double Staccato = 0.5;

    public static VoiceTrack Generate(ScoreDocument score, ChordOptions options)
    {
        var ppq = score.TicksPerQuarterNote;
        var notes = new List<NoteEvent>();
        var octaveBase = ChordVoicing.DefaultRootOctaveBase + (12 * options.OctaveShift);

        // Voice leading looks back at the chord before it, so the chords are voiced in order.
        IReadOnlyList<int>? previous = null;

        foreach (var chord in score.Chords)
        {
            if (chord.Symbol is not { } symbol || chord.DurationTicks <= 0)
            {
                continue;
            }

            var pitches = ChordVoicing.GetNotes(symbol, octaveBase, options.Inversion, previous);

            // The slash bass sits below the voicing and is not part of what the next chord moves towards.
            previous = symbol.BassPitchClass is null ? pitches : [.. pitches.Skip(1)];
            if (options.Pattern == ChordPattern.Block)
            {
                notes.AddRange(pitches.Select(pitch => new NoteEvent(chord.StartTicks, chord.DurationTicks, pitch, Velocity(options, onBeat: true))));
                continue;
            }

            var step = Math.Max(1, ppq / 2);
            var end = chord.StartTicks + chord.DurationTicks;
            var index = 0;

            // Steps stay on the absolute grid, so a chord starting off the beat does not shift the rhythm.
            for (var tick = chord.StartTicks; tick < end; index++)
            {
                var next = Math.Min(end, ((tick / step) + 1) * step);
                var onBeat = tick % ppq == 0;
                if (options.Pattern != ChordPattern.Offbeat || !onBeat)
                {
                    var length = (long)((next - tick) * (options.Pattern == ChordPattern.Offbeat ? Staccato : Detached));
                    var velocity = Velocity(options, onBeat);
                    foreach (var pitch in Voicing(options.Pattern, pitches, index))
                    {
                        notes.Add(new NoteEvent(tick, Math.Max(1, length), pitch, velocity));
                    }
                }

                tick = next;
            }
        }

        return new VoiceTrack(TrackId, TrackId, notes, TrackKind.Chords);
    }

    /// <summary>An arpeggio plays one note per step, the other patterns the whole chord.</summary>
    private static IEnumerable<int> Voicing(ChordPattern pattern, IReadOnlyList<int> pitches, int index) =>
        pattern == ChordPattern.ArpeggioUp ? [pitches[index % pitches.Count]] : pitches;

    private static int Velocity(ChordOptions options, bool onBeat) =>
        Math.Clamp(onBeat ? options.Velocity : options.Velocity - OffBeatSoftening, 1, 127);
}
