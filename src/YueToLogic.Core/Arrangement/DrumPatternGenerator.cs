using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

/// <summary>
/// A four-on-the-floor groove: kick on every beat, snare on every second beat, closed hi-hat on every
/// half beat, and optionally a crash cymbal (instead of the hi-hat) where a section starts.
/// Compound meters such as 6/8 count dotted quarters as beats and play the hi-hat on every eighth note.
/// </summary>
internal static class DrumPatternGenerator
{
    public const string TrackId = "Drums";

    private const int KickVelocity = 110;
    private const int SnareVelocity = 100;
    private const int CrashVelocity = 100;
    private const int HiHatOnBeatVelocity = 80;
    private const int HiHatOffBeatVelocity = 60;

    public static VoiceTrack Generate(ScoreDocument score, DrumOptions options)
    {
        var ppq = score.TicksPerQuarterNote;
        var hitTicks = Math.Max(1, ppq / 4);
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
            var hiHatTicks = Math.Max(1, compound ? noteTicks : beatTicks / 2);

            for (var bar = signature.StartTicks; bar < segmentEnd; bar += beatTicks * beatsPerBar)
            {
                for (var beat = 0; beat < beatsPerBar && bar + beat * beatTicks < segmentEnd; beat++)
                {
                    var tick = bar + beat * beatTicks;
                    notes.Add(new NoteEvent(tick, hitTicks, GeneralMidiDrums.Kick, KickVelocity));
                    if (beat % 2 == 1)
                    {
                        notes.Add(new NoteEvent(tick, hitTicks, GeneralMidiDrums.Snare, SnareVelocity));
                    }
                }
            }

            for (var tick = signature.StartTicks; tick < segmentEnd; tick += hiHatTicks)
            {
                if (!crashTicks.Contains(tick))
                {
                    var onBeat = (tick - signature.StartTicks) % beatTicks == 0;
                    notes.Add(new NoteEvent(tick, hitTicks, GeneralMidiDrums.ClosedHiHat, onBeat ? HiHatOnBeatVelocity : HiHatOffBeatVelocity));
                }
            }
        }

        notes.AddRange(crashTicks.Select(tick => new NoteEvent(tick, ppq, GeneralMidiDrums.Crash, CrashVelocity)));
        return new VoiceTrack(TrackId, TrackId, notes.OrderBy(n => n.StartTicks).ToArray(), TrackKind.Drums);
    }
}
