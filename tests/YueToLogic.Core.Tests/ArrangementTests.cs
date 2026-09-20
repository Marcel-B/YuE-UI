using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;
using YueToLogic.Core.Serialization;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class ArrangementTests
{
    private static readonly ScoreArranger Arranger = new();

    private static ScoreDocument Sample => ParseScore(File.ReadAllText(SamplePath));

    private static ScoreDocument TwoVoices => ParseScore(Native("""
        V: Vocal
        C16|
        V: Ins
        E16|
        """));

    [Fact]
    public void Default_options_leave_the_score_unchanged()
    {
        var result = Arranger.Arrange(Sample);

        Assert.Equal(Sample.Voices.Select(v => v.Notes), result.Score.Voices.Select(v => v.Notes));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Single_voice_is_moved_by_octaves()
    {
        var score = Arranger.Arrange(TwoVoices, new ArrangementOptions { OctaveShifts = new Dictionary<string, int> { ["vocal"] = 1 } }).Score;

        Assert.Equal([72], score.Voice("Vocal").Pitches());
        Assert.Equal([64], score.Voice("Ins").Pitches());
    }

    [Fact]
    public void Voice_specific_shift_takes_precedence_over_the_default()
    {
        var options = new ArrangementOptions
        {
            DefaultOctaveShift = -1,
            OctaveShifts = new Dictionary<string, int> { ["Ins"] = 2 },
        };

        var score = Arranger.Arrange(TwoVoices, options).Score;

        Assert.Equal([48], score.Voice("Vocal").Pitches());
        Assert.Equal([88], score.Voice("Ins").Pitches());
    }

    [Fact]
    public void Notes_moved_outside_the_midi_range_are_dropped_with_a_warning()
    {
        var score = ParseScore(Native("V: Vocal\nC8c'8|"));

        var result = Arranger.Arrange(score, new ArrangementOptions { DefaultOctaveShift = 4 });

        Assert.Equal([108], result.Score.Voice("Vocal").Pitches());
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.PitchOutOfRange);
    }

    [Fact]
    public void Unknown_voice_is_reported()
    {
        var result = Arranger.Arrange(Sample, new ArrangementOptions { OctaveShifts = new Dictionary<string, int> { ["Piano"] = 1 } });

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.UnknownVoice && d.Message.Contains("Vocal, Ins", StringComparison.Ordinal));
    }

    [Fact]
    public void Bass_plays_chord_roots_in_eighth_notes_in_the_bass_register()
    {
        var bass = Arranger.Arrange(Sample, new ArrangementOptions { Bass = new BassOptions() }).Score.Voice("Bass");

        Assert.Equal(TrackKind.Bass, bass.Kind);
        Assert.Equal(8 * 8, bass.Notes.Count);
        Assert.All(bass.Notes, n => Assert.Equal(0, n.StartTicks % (Ppq / 2)));
        // C, G, Am, F in the first four bars
        Assert.Equal([48, 43, 45, 41], bass.Notes.Where((_, i) => i % 8 == 0).Take(4).Select(n => n.NoteNumber));
        Assert.All(bass.Notes, n => Assert.InRange(n.NoteNumber, 40, 51));
        Assert.Equal([100, 84], bass.Notes.Take(2).Select(n => n.Velocity!.Value));
        Assert.True(bass.Notes[0].DurationTicks < Ppq / 2, "Bass notes are slightly detached.");
    }

    [Fact]
    public void Bass_uses_the_slash_bass_note()
    {
        var score = ParseScore(Native("V: Vocal\n\"C/E\"C16|"));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions() }).Score.Voice("Bass");

        Assert.All(bass.Notes, n => Assert.Equal(40, n.NoteNumber));
    }

    [Theory]
    [InlineData(-1, 36)]
    [InlineData(1, 60)]
    public void Bass_register_can_be_moved_by_octaves(int octaves, int expected)
    {
        var bass = Arranger.Arrange(Sample, new ArrangementOptions { Bass = new BassOptions { OctaveShift = octaves } }).Score.Voice("Bass");

        Assert.Equal(expected, bass.Notes[0].NoteNumber);
    }

    [Fact]
    public void Bass_quarter_notes()
    {
        var bass = Arranger.Arrange(Sample, new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.Quarters } }).Score.Voice("Bass");

        Assert.Equal(8 * 4, bass.Notes.Count);
    }

    [Theory]
    [InlineData("C", 48, 55)]
    [InlineData("Bdim", 47, 53)]
    [InlineData("C/G", 43, 48)]
    public void Root_fifth_alternates_bass_note_and_fifth(string chord, int bassNote, int alternate)
    {
        var score = ParseScore(Native($"V: Vocal\n\"{chord}\"C16|"));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.RootFifth } }).Score.Voice("Bass");

        Assert.Equal([bassNote, alternate, bassNote, alternate], bass.Pitches());
    }

    [Fact]
    public void Bass_stays_on_the_eighth_note_grid_when_a_chord_changes_off_the_beat()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C3\"G\"C13|"));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions() }).Score.Voice("Bass");

        Assert.Equal([0L, 240, 360, 480, 720], bass.Notes.Take(5).Select(n => n.StartTicks));
        Assert.Equal([48, 48, 43, 43, 43], bass.Pitches()[..5]);
    }

    [Fact]
    public void Bass_octaves_alternate_with_the_octave_above()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C16|"));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.Octaves } }).Score.Voice("Bass");

        Assert.Equal([48, 60, 48, 60, 48, 60, 48, 60], bass.Pitches());
    }

    [Fact]
    public void Bass_offbeat_plays_only_the_off_beat_eighths()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C16|"));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.Offbeat } }).Score.Voice("Bass");

        Assert.Equal([Ppq / 2, Ppq + (Ppq / 2), (2 * Ppq) + (Ppq / 2), (3 * Ppq) + (Ppq / 2)], bass.Notes.Select(n => n.StartTicks));
        Assert.All(bass.Notes, n => Assert.Equal(84, n.Velocity!.Value)); // off the beat, so slightly softer
    }

    [Fact]
    public void Sustained_bass_plays_one_note_per_chord()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C8\"G\"C8|"));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.Sustained } }).Score.Voice("Bass");

        Assert.Equal([(0L, 48), (2L * Ppq, 43)], bass.Notes.Select(n => (n.StartTicks, n.NoteNumber)));
        Assert.All(bass.Notes, n => Assert.InRange(n.DurationTicks, (2 * Ppq * 8) / 10, 2 * Ppq));
    }

    [Fact]
    public void Chords_are_only_played_out_when_a_pattern_is_chosen()
    {
        Assert.DoesNotContain(Arranger.Arrange(Sample).Score.Voices, v => v.Kind == TrackKind.Chords);

        var chords = Arranger.Arrange(Sample, new ArrangementOptions { Chords = new ChordOptions() }).Score.Voice("Chords");

        // Eight bars of one chord each, as block chords: the three notes of each triad, held for a whole bar.
        Assert.Equal(TrackKind.Chords, chords.Kind);
        Assert.Equal(8 * 3, chords.Notes.Count);
        Assert.Equal([48, 52, 55], chords.Pitches()[..3]); // C major with the root in octave 3
        Assert.All(chords.Notes, n => Assert.Equal(Bar, n.DurationTicks));
        Assert.All(chords.Notes, n => Assert.Equal(72, n.Velocity!.Value));
    }

    [Theory]
    [InlineData(ChordPattern.Eighths, 8, 0L, 72)]
    [InlineData(ChordPattern.Offbeat, 4, 240L, 62)]
    public void Chord_patterns_repeat_the_chord_on_their_grid(ChordPattern pattern, int hits, long firstTick, int firstVelocity)
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C16|"));

        var chords = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Pattern = pattern } }).Score.Voice("Chords");

        Assert.Equal(hits * 3, chords.Notes.Count);
        Assert.Equal(firstTick, chords.Notes[0].StartTicks);
        Assert.Equal(firstVelocity, chords.Notes[0].Velocity!.Value);
        Assert.Equal(hits, chords.Notes.Select(n => n.StartTicks).Distinct().Count());
        Assert.All(chords.Notes, n => Assert.True(n.DurationTicks < Ppq / 2, "Repeated chords are detached."));
    }

    [Fact]
    public void Arpeggio_plays_one_chord_note_per_eighth_from_the_bottom_up()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C16|"));

        var chords = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.ArpeggioUp } }).Score.Voice("Chords");

        Assert.Equal([48, 52, 55, 48, 52, 55, 48, 52], chords.Pitches());
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i * (Ppq / 2L)), chords.Notes.Select(n => n.StartTicks));
    }

    [Fact]
    public void Chord_register_can_be_moved_by_octaves()
    {
        var chords = Arranger.Arrange(Sample, new ArrangementOptions { Chords = new ChordOptions { OctaveShift = -1 } }).Score.Voice("Chords");

        Assert.Equal([36, 40, 43], chords.Pitches()[..3]);
    }

    [Fact]
    public void Half_time_plays_kick_on_one_and_snare_on_three()
    {
        var drums = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions { Pattern = DrumPattern.HalfTime } }).Score.Voice("Drums");

        Assert.Equal(8, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Kick));
        Assert.All(drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Kick), n => Assert.Equal(0, n.StartTicks % Bar));
        Assert.Equal(8, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Snare));
        Assert.All(drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Snare), n => Assert.Equal(2 * Ppq, n.StartTicks % Bar));
    }

    [Fact]
    public void Disco_opens_the_hi_hat_on_every_off_beat()
    {
        var drums = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions { Pattern = DrumPattern.Disco } }).Score.Voice("Drums");

        var open = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.OpenHiHat).ToList();

        Assert.Equal(32, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Kick));
        Assert.Equal(8 * 4, open.Count);
        Assert.All(open, n => Assert.Equal(Ppq / 2, n.StartTicks % Ppq));
        Assert.Equal((8 * 4) - 2, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.ClosedHiHat)); // 2 replaced by crashes
    }

    [Fact]
    public void Drums_play_four_on_the_floor_with_backbeat_hi_hats_and_section_crashes()
    {
        var drums = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions() }).Score.Voice("Drums");

        Assert.Equal(TrackKind.Drums, drums.Kind);
        var kicks = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Kick).ToList();
        var snares = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Snare).ToList();
        var crashes = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Crash).ToList();

        Assert.Equal(32, kicks.Count);
        Assert.All(kicks, n => Assert.Equal(0, n.StartTicks % Ppq));
        Assert.Equal([Ppq, 3L * Ppq], snares.Take(2).Select(n => n.StartTicks));
        Assert.Equal(16, snares.Count);
        Assert.Equal(64 - 2, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.ClosedHiHat));
        Assert.Equal([0L, 4 * Bar], crashes.Select(n => n.StartTicks));
    }

    [Fact]
    public void Backbeat_plays_kick_on_one_and_three_and_opens_the_last_hi_hat_of_the_bar()
    {
        var drums = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions { Pattern = DrumPattern.Backbeat } }).Score.Voice("Drums");

        var kicks = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Kick).Select(n => n.StartTicks % Bar).Distinct().Order();
        var open = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.OpenHiHat).ToList();

        Assert.Equal([0L, 2 * Ppq], kicks);
        Assert.Equal(16, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Kick));
        Assert.Equal(16, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Snare));
        Assert.Equal(8, open.Count);
        Assert.All(open, n => Assert.Equal(Bar - Ppq / 2, n.StartTicks % Bar)); // on "4+"
        Assert.Equal((7 * 8) - 2, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.ClosedHiHat)); // 2 replaced by crashes
    }

    [Fact]
    public void Crash_cymbals_can_be_switched_off()
    {
        var drums = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions { CrashOnSections = false } }).Score.Voice("Drums");

        Assert.DoesNotContain(drums.Notes, n => n.NoteNumber == GeneralMidiDrums.Crash);
        Assert.Equal(64, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.ClosedHiHat));
    }

    [Theory]
    [InlineData("3/4", "C12|", 3, 1, 6)]
    [InlineData("6/8", "C12|", 2, 1, 6)]
    [InlineData("2/2", "C16|", 2, 1, 4)]
    public void Drum_pattern_follows_the_meter(string meter, string bar, int kicks, int snares, int hiHats)
    {
        var score = ParseScore(Native($"V: Vocal\n{bar}", meter: meter));

        var drums = Arranger.Arrange(score, new ArrangementOptions { Drums = new DrumOptions() }).Score.Voice("Drums");

        Assert.Equal(kicks, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Kick));
        Assert.Equal(snares, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.Snare));
        Assert.Equal(hiHats, drums.Notes.Count(n => n.NoteNumber == GeneralMidiDrums.ClosedHiHat));
    }

    [Fact]
    public void Generated_tracks_follow_the_chord_track_and_drums_use_channel_ten()
    {
        var options = new ConversionOptions { Arrangement = new ArrangementOptions { Bass = new BassOptions(), Drums = new DrumOptions() } };

        var result = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);
        var tracks = MidiFile.Read(new MemoryStream(result.Midi!)).GetTrackChunks().ToList();

        Assert.Equal(
            ["Conductor", "Vocal", "Ins", "Chords", "Bass", "Drums"],
            tracks.Select(t => t.Events.OfType<SequenceTrackNameEvent>().Single().Text));
        Assert.All(tracks[5].Events.OfType<NoteOnEvent>(), e => Assert.Equal(9, (int)e.Channel));
        Assert.All(tracks[4].Events.OfType<NoteOnEvent>(), e => Assert.Equal(3, (int)e.Channel));
        Assert.Equal(110, (int)tracks[5].Events.OfType<NoteOnEvent>().First(e => e.NoteNumber == GeneralMidiDrums.Kick).Velocity);
    }

    [Fact]
    public void Partial_json_options_keep_their_defaults()
    {
        var options = JsonSerializer.Deserialize(
            """{"arrangement":{"bass":{"pattern":"rootFifth"},"drums":{}}}""",
            YueToLogicJsonContext.Default.ConversionOptions)!;

        Assert.Equal(480, options.TicksPerQuarterNote);
        Assert.True(options.IncludeChordTrack);
        Assert.Equal(BassPattern.RootFifth, options.Arrangement.Bass!.Pattern);
        Assert.Equal(100, options.Arrangement.Bass.Velocity);
        Assert.True(options.Arrangement.Drums!.CrashOnSections);
        Assert.Empty(ConversionOptionsValidator.Validate(options));
    }

    [Fact]
    public void Arrangement_options_round_trip_through_json()
    {
        var options = new ConversionOptions
        {
            Arrangement = new ArrangementOptions
            {
                DefaultOctaveShift = -1,
                OctaveShifts = new Dictionary<string, int> { ["Vocal"] = 1 },
                Bass = new BassOptions { Pattern = BassPattern.RootFifth },
                Drums = new DrumOptions { CrashOnSections = false },
            },
        };

        var json = JsonSerializer.Serialize(options, YueToLogicJsonContext.Default.ConversionOptions);
        var restored = JsonSerializer.Deserialize(json, YueToLogicJsonContext.Default.ConversionOptions)!;

        Assert.Contains("\"pattern\": \"RootFifth\"", json, StringComparison.Ordinal);
        Assert.Equal(1, restored.Arrangement.OctaveShifts["Vocal"]);
        Assert.Equal(options.Arrangement.Bass, restored.Arrangement.Bass);
        Assert.Equal(options.Arrangement.Drums, restored.Arrangement.Drums);
        Assert.Equal(-1, restored.Arrangement.DefaultOctaveShift);
    }
}
