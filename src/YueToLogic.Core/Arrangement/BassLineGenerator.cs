using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>Plays the bass note of every chord symbol on the rhythmic grid of the chosen pattern.</summary>
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
        var step = options.Pattern switch
        {
            BassPattern.Eighths or BassPattern.Octaves or BassPattern.Offbeat => Math.Max(1, ppq / 2),
            BassPattern.Sustained => long.MaxValue,
            _ => ppq,
        };
        var notes = new List<NoteEvent>();
        var chords = score.Chords.Where(c => c.Symbol is not null).ToList();

        for (var c = 0; c < chords.Count; c++)
        {
            var chord = chords[c];
            var symbol = chord.Symbol!;

            // A slash chord (C/E) names the bass note explicitly.
            var bassNote = LowestNote + (12 * options.OctaveShift) + Mod12((symbol.BassPitchClass ?? symbol.RootPitchClass) - LowestNote);
            var alternateNote = AlternateNote(symbol, bassNote);
            var approach = c + 1 < chords.Count ? ApproachNote(bassNote, BassNoteOf(chords[c + 1].Symbol!, options)) : (int?)null;
            var end = chord.StartTicks + chord.DurationTicks;
            var index = 0;

            // Steps stay on the absolute grid, so a chord starting off the beat does not shift the rhythm.
            for (var tick = chord.StartTicks; tick < end; index++)
            {
                var next = step == long.MaxValue ? end : Math.Min(end, ((tick / step) + 1) * step);
                var onBeat = tick % ppq == 0;
                var last = next >= end;
                if (options.Pattern != BassPattern.Offbeat || !onBeat)
                {
                    var pitch = options.Pattern switch
                    {
                        BassPattern.RootFifth when index % 2 == 1 => alternateNote,
                        BassPattern.Octaves when index % 2 == 1 => bassNote + 12,
                        BassPattern.Walking when last && index > 0 && approach is { } target => target,
                        BassPattern.Walking => ChordTone(symbol, bassNote, index),
                        _ => bassNote,
                    };
                    var velocity = onBeat ? options.Velocity : options.Velocity - OffBeatSoftening;

                    // Slightly detached so that repeated notes are audible as separate attacks.
                    var duration = Math.Max(1, (next - tick) * 9 / 10);
                    notes.Add(new NoteEvent(tick, duration, pitch, Math.Clamp(velocity, 1, 127)));
                }

                tick = next;
            }
        }

        return new VoiceTrack(TrackId, TrackId, notes, TrackKind.Bass);
    }

    /// <summary>The bass note a chord is played on, in the register the options ask for.</summary>
    private static int BassNoteOf(ChordSymbol symbol, BassOptions options)
    {
        var lowest = LowestNote + (12 * options.OctaveShift);
        return lowest + Mod12((symbol.BassPitchClass ?? symbol.RootPitchClass) - lowest);
    }

    /// <summary>A semitone below the note the next chord starts on, or above it when the line is coming down.</summary>
    private static int ApproachNote(int from, int target) =>
        Math.Clamp(from <= target ? target - 1 : target + 1, 0, 127);

    /// <summary>The notes of the chord above its bass note, one per step: root, third, fifth, and so on.</summary>
    private static int ChordTone(ChordSymbol symbol, int bassNote, int index)
    {
        var intervals = ChordVoicing.GetIntervals(symbol.Quality);
        var pitchClass = symbol.RootPitchClass + intervals[index % intervals.Count];
        return bassNote + Mod12(pitchClass - bassNote);
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
