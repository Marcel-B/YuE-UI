namespace YueToLogic.Core.Model;

/// <summary>
/// A score resolved to absolute MIDI ticks. Independent of ABC and of MIDI files, so hosts can
/// serialize it as JSON and render it (piano roll, arrangement view) without re-parsing.
/// </summary>
public sealed record ScoreDocument(
    int TicksPerQuarterNote,
    string? Title,
    double TempoBpm,
    IReadOnlyList<TimeSignatureChange> TimeSignatures,
    IReadOnlyList<KeySignatureChange> KeySignatures,
    IReadOnlyList<SectionMarker> Sections,
    // Score voices first, followed by generated accompaniment tracks, if any.
    IReadOnlyList<VoiceTrack> Voices,
    IReadOnlyList<ChordEvent> Chords,
    long LengthTicks)
{
    public double DurationSeconds => LengthTicks / (double)TicksPerQuarterNote * 60.0 / TempoBpm;

    /// <summary>Converts an absolute tick position into a 1-based bar and beat, honouring meter changes.</summary>
    public BarPosition GetBarPosition(long ticks)
    {
        var barsBefore = 0L;
        for (var i = 0; i < TimeSignatures.Count; i++)
        {
            var signature = TimeSignatures[i];
            var beatTicks = 4L * TicksPerQuarterNote / signature.Denominator;
            var measureTicks = beatTicks * signature.Numerator;
            var segmentEnd = i + 1 < TimeSignatures.Count ? TimeSignatures[i + 1].StartTicks : long.MaxValue;

            if (ticks < segmentEnd)
            {
                var offset = Math.Max(0, ticks - signature.StartTicks);
                var bar = barsBefore + offset / measureTicks + 1;
                var beat = (offset % measureTicks) / (double)beatTicks + 1;
                return new BarPosition((int)bar, beat);
            }

            barsBefore += (segmentEnd - signature.StartTicks + measureTicks - 1) / measureTicks;
        }

        return new BarPosition(1, 1);
    }
}

public readonly record struct BarPosition(int Bar, double Beat);

public sealed record TimeSignatureChange(long StartTicks, int Numerator, int Denominator);

/// <param name="Sharps">Number of sharps (positive) or flats (negative), as in a MIDI key signature.</param>
public sealed record KeySignatureChange(long StartTicks, string Key, int Sharps, bool IsMinor);

/// <summary>A song section taken from an ABC comment such as <c>% verse</c>.</summary>
public sealed record SectionMarker(long StartTicks, string Name);

/// <summary>A voice from the score, or an accompaniment track generated from it (see <see cref="Kind"/>).</summary>
public sealed record VoiceTrack(string Id, string DisplayName, IReadOnlyList<NoteEvent> Notes, TrackKind Kind = TrackKind.Melody);

public enum TrackKind
{
    /// <summary>A voice read from the score (<c>Vocal</c>, <c>Ins</c>).</summary>
    Melody,

    /// <summary>Generated chord track: the chord symbols of the score played out.</summary>
    Chords,

    /// <summary>Generated bass line following the chords.</summary>
    Bass,

    /// <summary>Generated drum pattern; note numbers follow the General MIDI drum map.</summary>
    Drums,

    /// <summary>Generated guide-tone track: the third and seventh of every chord, held as a pad.</summary>
    GuideTones,

    /// <summary>A score voice copied to a second track at another octave.</summary>
    Doubling,
}
