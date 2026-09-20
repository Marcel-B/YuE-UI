using System.Text;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using Microsoft.Extensions.DependencyInjection;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Serialization;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class ScoreConverterTests
{
    [Fact]
    public void Sample_becomes_a_type_1_midi_file_with_conductor_voice_and_chord_tracks()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath));

        Assert.True(result.Success);
        var midi = MidiFile.Read(new MemoryStream(result.Midi!));
        var tracks = midi.GetTrackChunks().ToList();

        Assert.Equal(MidiFileFormat.MultiTrack, midi.OriginalFormat);
        Assert.Equal(Ppq, ((TicksPerQuarterNoteTimeDivision)midi.TimeDivision).TicksPerQuarterNote);
        Assert.Equal(["Conductor", "Vocal", "Ins", "Chords"], tracks.Select(TrackName));

        var conductor = tracks[0].Events;
        Assert.Equal(681_818, conductor.OfType<SetTempoEvent>().Single().MicrosecondsPerQuarterNote);
        Assert.Equal((4, 4), conductor.OfType<TimeSignatureEvent>().Select(t => ((int)t.Numerator, (int)t.Denominator)).Single());
        Assert.Equal(["verse", "chorus"], conductor.OfType<MarkerEvent>().Select(m => m.Text));

        Assert.Equal(56, NoteOns(tracks[1]));
        Assert.Equal(0, NoteOns(tracks[2]));
        Assert.Equal(8 * 3, NoteOns(tracks[3]));
        Assert.Equal(8, tracks[3].Events.OfType<TextEvent>().Count());
    }

    [Fact]
    public void Chord_pattern_replaces_the_block_chords_of_the_midi_chord_track()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.Eighths } },
        };

        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);

        var tracks = MidiFile.Read(new MemoryStream(result.Midi!)).GetTrackChunks().ToList();
        Assert.Equal(["Conductor", "Vocal", "Ins", "Chords"], tracks.Select(TrackName));

        // Eight bars of eight eighth-note chords with three notes each, and still one text event per chord symbol.
        var chords = tracks[3];
        Assert.Equal(8 * 8 * 3, NoteOns(chords));
        Assert.Equal(8, chords.Events.OfType<TextEvent>().Count());
        Assert.Equal(result.Score!.Voice("Chords").Notes.Count, NoteOns(chords));
    }

    [Fact]
    public void Chord_track_can_be_left_out()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), new ConversionOptions { IncludeChordTrack = false });

        Assert.Equal(3, MidiFile.Read(new MemoryStream(result.Midi!)).GetTrackChunks().Count());
    }

    [Fact]
    public void Tied_notes_become_a_single_midi_note()
    {
        var result = new ScoreConverter().Convert(Native("V: Vocal\nC16-|C16|"));
        var vocal = MidiFile.Read(new MemoryStream(result.Midi!)).GetTrackChunks().ElementAt(1);

        Assert.Equal(1, NoteOns(vocal));
        Assert.Equal(2 * Bar, vocal.Events.OfType<NoteOffEvent>().Single().DeltaTime);
    }

    [Fact]
    public void Failed_conversion_returns_diagnostics_instead_of_throwing()
    {
        var result = new ScoreConverter().Convert("X:1\nK:C\n");

        Assert.False(result.Success);
        Assert.Null(result.Midi);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task Stream_input_with_byte_order_mark_is_supported()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(File.ReadAllText(SamplePath))).ToArray();

        var result = await new ScoreConverter().ConvertAsync(new MemoryStream(bytes));

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Result_round_trips_through_json()
    {
        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath));

        var json = JsonSerializer.Serialize(result, YueToLogicJsonContext.Default.ConversionResult);
        var restored = JsonSerializer.Deserialize(json, YueToLogicJsonContext.Default.ConversionResult)!;

        Assert.Contains("\"quality\": \"Minor\"", json, StringComparison.Ordinal);
        Assert.Equal(result.Midi, restored.Midi);
        Assert.Equal(result.Score!.Voice("Vocal").Notes, restored.Score!.Voice("Vocal").Notes);
        Assert.Equal(result.Score!.Chords, restored.Score!.Chords);
    }

    [Fact]
    public void Services_can_be_resolved_from_dependency_injection()
    {
        using var provider = new ServiceCollection().AddYueToLogic().BuildServiceProvider();

        var converter = provider.GetRequiredService<IScoreConverter>();

        Assert.True(converter.Convert(File.ReadAllText(SamplePath)).Success);
    }

    private static string TrackName(TrackChunk track) =>
        track.Events.OfType<SequenceTrackNameEvent>().Single().Text;

    private static int NoteOns(TrackChunk track) => track.Events.OfType<NoteOnEvent>().Count();
}
