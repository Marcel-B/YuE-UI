using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// Drum grooves on the score's meter, all of them with a hi-hat on every half beat: <see cref="DrumPattern.FourOnTheFloor"/>
/// puts the kick on every beat, <see cref="DrumPattern.Backbeat"/> on every other beat plus an open hi-hat on the last
/// half beat of the bar, <see cref="DrumPattern.HalfTime"/> plays kick and snare once per bar, and
/// <see cref="DrumPattern.Disco"/> combines the kick on every beat with an open hi-hat on every off-beat. The snare
/// falls on every second beat, in half time only in the middle of the bar. Optionally a crash cymbal replaces the
/// hi-hat where a section starts. Compound meters such as 6/8 count dotted quarters as beats and play the hi-hat on
/// every eighth.
/// </summary>
internal static class DrumPatternGenerator
{
    public const string TrackId = "Drums";

    private const int KickVelocity = 110;
    private const int SnareVelocity = 100;
    private const int CrashVelocity = 100;
    private const int HiHatOnBeatVelocity = 80;
    private const int HiHatOffBeatVelocity = 60;
    private const int OpenHiHatVelocity = 75;

    /// <summary>Every hi-hat position of the segment: each beat opens one, the offsets fill it.</summary>
    private static IEnumerable<long> HiHatTicks(long start, long end, long beatTicks, int beatsPerBar, long[] offsets)
    {
        for (var bar = start; bar < end; bar += beatTicks * beatsPerBar)
        {
            for (var beat = 0; beat < beatsPerBar; beat++)
            {
                foreach (var offset in offsets)
                {
                    var tick = bar + (beat * beatTicks) + offset;
                    if (tick < end)
                    {
                        yield return tick;
                    }
                }
            }
        }
    }

    public static VoiceTrack Generate(ScoreDocument score, DrumOptions options)
    {
        var ppq = score.TicksPerQuarterNote;
        var hitTicks = Math.Max(1, ppq / 4);
        var pattern = options.Pattern;
        var crashTicks = options.CrashOnSections
            ? score.Sections.Select(s => s.StartTicks).Where(t => t < score.LengthTicks).ToHashSet()
            : [];
        var notes = new List<NoteEvent>();

        for (var i = 0; i < score.TimeSignatures.Count; i++)
        {
            var signature = score.TimeSignatures[i];
            var segmentEnd = i + 1 < score.TimeSignatures.Count ? score.TimeSignatures[i + 1].StartTicks : score.LengthTicks;
            var compound = signature.Denominator == 8 && signature.Numerator > 3 && signature.Numerator % 3 == 0;
            var noteTicks = 4L * ppq / signature.Denominator;
            var beatTicks = compound ? 3 * noteTicks : noteTicks;
            var beatsPerBar = compound ? signature.Numerator / 3 : signature.Numerator;
            // Where the hi-hat falls inside a beat: halves, quarters, or the first and third triplet.
            long[] divisions = compound
                ? [noteTicks, 2 * noteTicks]
                : pattern switch
                {
                    DrumPattern.SixteenthHats => [beatTicks / 4, beatTicks / 2, 3 * beatTicks / 4],
                    DrumPattern.Shuffle => [2 * beatTicks / 3],
                    _ => [beatTicks / 2],
                };
            long[] offsets = [0, .. divisions.Where(d => d > 0)];
            var hiHatTicks = Math.Max(1, offsets.Length > 1 ? offsets[1] : beatTicks);

            for (var bar = signature.StartTicks; bar < segmentEnd; bar += beatTicks * beatsPerBar)
            {
                for (var beat = 0; beat < beatsPerBar && bar + beat * beatTicks < segmentEnd; beat++)
                {
                    var tick = bar + beat * beatTicks;
                    var kick = pattern switch
                    {
                        DrumPattern.Backbeat or DrumPattern.SixteenthHats or DrumPattern.Shuffle => beat % 2 == 0,
                        DrumPattern.HalfTime => beat == 0,
                        _ => true,
                    };
                    if (kick)
                    {
                        notes.Add(new NoteEvent(tick, hitTicks, GeneralMidiDrums.Kick, KickVelocity));
                    }

                    var snare = pattern == DrumPattern.HalfTime ? beat == beatsPerBar / 2 : beat % 2 == 1;
                    if (snare && beat > 0)
                    {
                        notes.Add(new NoteEvent(tick, hitTicks, GeneralMidiDrums.Snare, SnareVelocity));
                    }
                }
            }

            var measureTicks = beatTicks * beatsPerBar;
            foreach (var tick in HiHatTicks(signature.StartTicks, segmentEnd, beatTicks, beatsPerBar, offsets))
            {
                if (crashTicks.Contains(tick))
                {
                    continue;
                }

                var positionInBar = (tick - signature.StartTicks) % measureTicks;
                var onBeat = positionInBar % beatTicks == 0;
                var open = pattern switch
                {
                    // Open hi-hat on the last half beat ("4+"), ringing into the next downbeat.
                    DrumPattern.Backbeat => positionInBar + hiHatTicks >= measureTicks,
                    DrumPattern.Disco => !onBeat,
                    _ => false,
                };
                if (open)
                {
                    notes.Add(new NoteEvent(tick, hiHatTicks, GeneralMidiDrums.OpenHiHat, OpenHiHatVelocity));
                    continue;
                }

                notes.Add(new NoteEvent(tick, hitTicks, GeneralMidiDrums.ClosedHiHat, onBeat ? HiHatOnBeatVelocity : HiHatOffBeatVelocity));
            }
        }

        notes.AddRange(crashTicks.Select(tick => new NoteEvent(tick, ppq, GeneralMidiDrums.Crash, CrashVelocity)));
        return new VoiceTrack(TrackId, TrackId, notes.OrderBy(n => n.StartTicks).ToArray(), TrackKind.Drums);
    }
}
