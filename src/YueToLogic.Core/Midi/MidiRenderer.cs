using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Midi;

public interface IMidiRenderer
{
    byte[] Render(ScoreDocument score, MidiRenderOptions? options = null);

    void Render(ScoreDocument score, Stream destination, MidiRenderOptions? options = null);
}

public sealed record MidiRenderOptions
{
    public bool IncludeChordTrack { get; init; } = true;

    public int MelodyVelocity { get; init; } = 96;

    public int ChordVelocity { get; init; } = 72;

    /// <summary>MIDI channel per track name (1-16); a track without an entry gets the next free one.</summary>
    public IReadOnlyDictionary<string, int> Channels { get; init; } = new Dictionary<string, int>();

    /// <summary>Program change (1-128) sent at the start of a track, per track name.</summary>
    public IReadOnlyDictionary<string, int> Programs { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// Writes a <see cref="ScoreDocument"/> as a Standard MIDI File, type 1: a conductor track with tempo,
/// meter, key and section markers, one track per voice, a track with the chords played as block chords,
/// and any generated bass and drum tracks (drums on General MIDI channel 10). Stateless and thread-safe.
/// </summary>
public sealed class MidiRenderer : IMidiRenderer
{
    public const string ConductorTrackName = "Conductor";
    public const string ChordTrackName = "Chords";

    // Events at the same tick: meta events first, then note-offs before note-ons so that
    // repeated notes are re-attacked instead of being cut short.
    private const int MetaOrder = 0;
    private const int NoteOffOrder = 1;
    private const int NoteOnOrder = 2;

    public byte[] Render(ScoreDocument score, MidiRenderOptions? options = null)
    {
        using var buffer = new MemoryStream();
        Render(score, buffer, options);
        return buffer.ToArray();
    }

    public void Render(ScoreDocument score, Stream destination, MidiRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new MidiRenderOptions();

        // Track order in Logic: melodies and their doublings, chords, then the generated accompaniment.
        var chunks = new List<TrackChunk> { BuildConductorTrack(score) };
        var channels = new ChannelAssignment(options.Channels);
        foreach (var voice in score.Voices.Where(v => v.Kind is TrackKind.Melody or TrackKind.Doubling))
        {
            chunks.Add(BuildVoiceTrack(voice, channels.Of(voice.Id), options.MelodyVelocity, [], Program(options, voice.Id)));
        }

        // The arranger plays the chord symbols; only a score that has not been through it needs block chords here.
        var chordVoice = score.Voices.FirstOrDefault(v => v.Kind == TrackKind.Chords);
        if (options.IncludeChordTrack && chordVoice is null && score.Chords.Count > 0)
        {
            chunks.Add(BuildChordTrack(score.Chords, channels.Of(ChordTrackName), options.ChordVelocity, Program(options, ChordTrackName)));
        }

        foreach (var voice in score.Voices.Where(v => v.Kind is not (TrackKind.Melody or TrackKind.Doubling)))
        {
            if (voice == chordVoice && !options.IncludeChordTrack)
            {
                continue;
            }

            var trackChannel = channels.Of(voice.Id, voice.Kind == TrackKind.Drums ? GeneralMidiDrums.Channel : null);
            var names = voice == chordVoice ? score.Chords : [];
            chunks.Add(BuildVoiceTrack(voice, trackChannel, options.MelodyVelocity, names, Program(options, voice.Id)));
        }

        var file = new MidiFile(chunks)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision((short)score.TicksPerQuarterNote),
        };
        file.Write(destination, MidiFileFormat.MultiTrack);
    }

    private static TrackChunk BuildConductorTrack(ScoreDocument score)
    {
        var events = new List<TimedMidiEvent>
        {
            new(0, MetaOrder, new SequenceTrackNameEvent(ConductorTrackName)),
            new(0, MetaOrder, new SetTempoEvent((long)Math.Round(60_000_000 / score.TempoBpm))),
        };

        if (score.Title is { } title)
        {
            events.Add(new TimedMidiEvent(0, MetaOrder, new TextEvent(title)));
        }

        events.AddRange(score.TimeSignatures.Select(ts =>
            new TimedMidiEvent(ts.StartTicks, MetaOrder, new TimeSignatureEvent((byte)ts.Numerator, (byte)ts.Denominator))));
        events.AddRange(score.KeySignatures.Select(ks =>
            new TimedMidiEvent(ks.StartTicks, MetaOrder, new KeySignatureEvent((sbyte)ks.Sharps, (byte)(ks.IsMinor ? 1 : 0)))));
        events.AddRange(score.Sections.Select(section =>
            new TimedMidiEvent(section.StartTicks, MetaOrder, new MarkerEvent(section.Name))));

        return ToTrackChunk(events);
    }

    /// <param name="chordNames">Chord symbols written as text events, for the generated chord track.</param>
    /// <param name="program">Program change to open the track with (1-128), or <c>null</c> for none.</param>
    private static TrackChunk BuildVoiceTrack(
        VoiceTrack voice,
        FourBitNumber channel,
        int defaultVelocity,
        IReadOnlyList<ChordEvent> chordNames,
        int? program)
    {
        var events = new List<TimedMidiEvent> { new(0, MetaOrder, new SequenceTrackNameEvent(voice.Id)) };
        AddProgram(events, channel, program);
        events.AddRange(chordNames.Select(chord => new TimedMidiEvent(chord.StartTicks, MetaOrder, new TextEvent(chord.Text))));
        foreach (var note in voice.Notes)
        {
            AddNote(events, note.StartTicks, note.DurationTicks, note.NoteNumber, channel, note.Velocity ?? defaultVelocity);
        }

        return ToTrackChunk(events);
    }

    private static TrackChunk BuildChordTrack(IReadOnlyList<ChordEvent> chords, FourBitNumber channel, int velocity, int? program)
    {
        var events = new List<TimedMidiEvent> { new(0, MetaOrder, new SequenceTrackNameEvent(ChordTrackName)) };
        AddProgram(events, channel, program);
        foreach (var chord in chords)
        {
            events.Add(new TimedMidiEvent(chord.StartTicks, MetaOrder, new TextEvent(chord.Text)));
            if (chord.Symbol is null)
            {
                continue;
            }

            foreach (var noteNumber in ChordVoicing.GetNotes(chord.Symbol))
            {
                AddNote(events, chord.StartTicks, chord.DurationTicks, noteNumber, channel, velocity);
            }
        }

        return ToTrackChunk(events);
    }

    private static void AddNote(List<TimedMidiEvent> events, long start, long duration, int noteNumber, FourBitNumber channel, int velocity)
    {
        var number = (SevenBitNumber)(byte)Math.Clamp(noteNumber, 0, 127);
        var noteVelocity = (SevenBitNumber)(byte)Math.Clamp(velocity, 1, 127);
        events.Add(new TimedMidiEvent(start, NoteOnOrder, new NoteOnEvent(number, noteVelocity) { Channel = channel }));
        events.Add(new TimedMidiEvent(start + duration, NoteOffOrder, new NoteOffEvent(number, SevenBitNumber.MinValue) { Channel = channel }));
    }

    private static TrackChunk ToTrackChunk(IEnumerable<TimedMidiEvent> events)
    {
        var chunk = new TrackChunk();
        var previous = 0L;
        foreach (var timed in events.OrderBy(e => e.Ticks).ThenBy(e => e.Order))
        {
            timed.Event.DeltaTime = timed.Ticks - previous;
            previous = timed.Ticks;
            chunk.Events.Add(timed.Event);
        }

        return chunk;
    }

    private static int? Program(MidiRenderOptions options, string track) =>
        options.Programs.TryGetValue(track, out var program) ? program : null;

    /// <summary>A program change at the start of the track, so the receiving instrument loads its sound.</summary>
    private static void AddProgram(List<TimedMidiEvent> events, FourBitNumber channel, int? program)
    {
        if (program is { } number)
        {
            var value = (SevenBitNumber)(byte)Math.Clamp(number - 1, 0, 127);
            events.Add(new TimedMidiEvent(0, MetaOrder, new ProgramChangeEvent(value) { Channel = channel }));
        }
    }

    /// <summary>
    /// Hands out the channel of every track: the one it was given, or the next one no track asked for.
    /// Drums stay on the General MIDI drum channel unless told otherwise.
    /// </summary>
    private sealed class ChannelAssignment
    {
        private readonly Dictionary<string, int> given;
        private readonly HashSet<int> taken;
        private int next;

        public ChannelAssignment(IReadOnlyDictionary<string, int> channels)
        {
            given = new Dictionary<string, int>(channels, StringComparer.OrdinalIgnoreCase);
            taken = [.. given.Values.Where(c => c is >= 1 and <= 16).Select(c => c - 1)];
        }

        public FourBitNumber Of(string track, int? preferred = null)
        {
            if (given.TryGetValue(track, out var channel) && channel is >= 1 and <= 16)
            {
                return (FourBitNumber)(byte)(channel - 1);
            }

            if (preferred is { } fallback && taken.Add(fallback))
            {
                return (FourBitNumber)(byte)fallback;
            }

            while (next < 15 && (taken.Contains(next) || next == GeneralMidiDrums.Channel))
            {
                next++;
            }

            taken.Add(next);
            return (FourBitNumber)(byte)Math.Min(next++, 15);
        }
    }

    private readonly record struct TimedMidiEvent(long Ticks, int Order, MidiEvent Event);
}
