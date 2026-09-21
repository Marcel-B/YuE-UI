using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;
using YueToLogic.Core.Serialization;
using static YueToLogic.Core.Tests.TestScores;
using NoteEvent = YueToLogic.Core.Model.NoteEvent;

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
    public void Walking_bass_steps_through_the_chord_and_approaches_the_next_one()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C16|"F"C16|
            V: Ins
            Z2|
            """));

        var bass = Arranger.Arrange(score, new ArrangementOptions { Bass = new BassOptions { Pattern = BassPattern.Walking } }).Score.Voice("Bass");

        // C: root, third, fifth, then a semitone above the F the next chord starts on, since the line comes
        // down to it. The last chord has nothing to approach, so it stays on its own notes.
        Assert.Equal([48, 52, 55, 42, 41, 45, 48, 41], bass.Pitches());
        Assert.All(bass.Notes, n => Assert.Equal(0, n.StartTicks % Ppq));
    }

    [Fact]
    public void Chords_can_run_up_and_back_down()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C16|"));

        var chords = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.ArpeggioUpDown } }).Score.Voice("Chords");

        Assert.Equal([48, 52, 55, 52, 48, 52, 55, 52], chords.Pitches());
    }

    [Fact]
    public void Chords_in_sixteenths_hit_four_times_per_beat()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"C16|"));

        var chords = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.Sixteenths } }).Score.Voice("Chords");

        Assert.Equal(16 * 3, chords.Notes.Count);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => i * (Ppq / 4L)), chords.Notes.Select(n => n.StartTicks).Distinct());
    }

    [Theory]
    [InlineData(DrumPattern.SixteenthHats, 16, 4)]
    [InlineData(DrumPattern.Shuffle, 8, 2)]
    public void Hi_hats_follow_the_pattern(DrumPattern pattern, int hitsPerBar, int hitsPerBeat)
    {
        var score = ParseScore(Native("V: Vocal\nC16|"));

        var drums = Arranger.Arrange(score, new ArrangementOptions { Drums = new DrumOptions { Pattern = pattern, CrashOnSections = false } }).Score.Voice("Drums");

        var hats = drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.ClosedHiHat).ToList();
        Assert.Equal(hitsPerBar, hats.Count);
        Assert.Equal(hitsPerBeat, hats.Count(n => n.StartTicks < Ppq));
        Assert.Equal([0L, 2 * Ppq], drums.Notes.Where(n => n.NoteNumber == GeneralMidiDrums.Kick).Select(n => n.StartTicks));

        // The shuffle puts its second hi-hat on the last third of the beat.
        if (pattern == DrumPattern.Shuffle)
        {
            Assert.Equal(2 * Ppq / 3, hats[1].StartTicks);
        }
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


    // ---- Chord inversions ------------------------------------------------------------------------

    [Theory]
    [InlineData(ChordInversion.RootPosition, new[] { 48, 52, 55 })]
    [InlineData(ChordInversion.First, new[] { 52, 55, 60 })]
    [InlineData(ChordInversion.Second, new[] { 55, 60, 64 })]
    public void Chord_inversions_keep_the_voicing_in_its_register(ChordInversion inversion, int[] expected)
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C16|
            V: Ins
            Z1|
            """));

        var chords = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Inversion = inversion } }).Score;

        Assert.Equal(expected, chords.Voice("Chords").Pitches());
    }

    [Fact]
    public void Closest_voicing_moves_the_chords_as_little_as_possible()
    {
        // Root position would jump C-F-G over an octave; the nearest inversions stay inside one.
        var score = ParseScore(Native("""
            V: Vocal
            "C"C16|"F"C16|"G"C16|
            V: Ins
            Z3|
            """));

        var closest = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Inversion = ChordInversion.Closest } }).Score;

        var voicings = closest.Voice("Chords").Notes.GroupBy(n => n.StartTicks).Select(g => g.Select(n => n.NoteNumber).Order().ToArray()).ToList();
        Assert.Equal([48, 52, 55], voicings[0]);
        Assert.Equal([48, 53, 57], voicings[1]); // F with its fifth at the bottom, a semitone from the C before it
        Assert.Equal([50, 55, 59], voicings[2]); // G the same way, rather than jumping up to its root

        // Every voicing stays inside one octave of the register the chord track lives in.
        Assert.All(closest.Voice("Chords").Pitches(), pitch => Assert.InRange(pitch, 48, 71));
    }

    [Fact]
    public void A_slash_bass_stays_below_the_inversion()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C/E"C16|
            V: Ins
            Z1|
            """));

        var chords = Arranger.Arrange(score, new ArrangementOptions { Chords = new ChordOptions { Inversion = ChordInversion.Second } }).Score;

        Assert.Equal([40, 55, 60, 64], chords.Voice("Chords").Pitches());
    }

    // ---- Guide tones -----------------------------------------------------------------------------

    [Fact]
    public void Guide_tones_are_the_third_and_the_seventh()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "Cmaj7"C16|"Dm7"C16|
            V: Ins
            Z2|
            """));

        var guide = Arranger.Arrange(score, new ArrangementOptions { GuideTones = new GuideToneOptions() }).Score.Voice("Guide");

        // Cmaj7: E and B; Dm7: F and C - each placed in the octave above MIDI 52.
        Assert.Equal([52, 59, 53, 60], guide.Pitches());
        Assert.All(guide.Notes, note => Assert.Equal(64, note.Velocity));
    }

    [Fact]
    public void A_triad_falls_back_to_its_fifth_and_neighbouring_chords_are_held()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C16|"C"C16|"Am"C16|
            V: Ins
            Z3|
            """));

        var guide = Arranger.Arrange(score, new ArrangementOptions { GuideTones = new GuideToneOptions() }).Score.Voice("Guide");

        // C major twice: E and G, held as one note over both bars instead of being struck again;
        // then A minor, whose third and fifth are C and E.
        Assert.Equal([52, 55, 52, 60], guide.Pitches());
        Assert.Equal([0, 0, 2 * Bar, 2 * Bar], guide.Notes.Select(n => n.StartTicks));
        Assert.Equal([2 * Bar, 2 * Bar, Bar, Bar], guide.Notes.Select(n => n.DurationTicks));
    }

    [Fact]
    public void Guide_tones_without_chords_are_reported_instead_of_written()
    {
        var result = Arranger.Arrange(TwoVoices, new ArrangementOptions { GuideTones = new GuideToneOptions() });

        Assert.DoesNotContain(result.Score.Voices, v => v.Kind == TrackKind.GuideTones);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.NoChords);
    }

    // ---- Doubling --------------------------------------------------------------------------------

    [Fact]
    public void The_vocal_is_doubled_on_a_track_of_its_own()
    {
        var result = Arranger.Arrange(TwoVoices, new ArrangementOptions { Doubling = new DoublingOptions() });

        var doubled = result.Score.Voice("Vocal 8vb");
        Assert.Equal(TrackKind.Doubling, doubled.Kind);
        Assert.Equal([48], doubled.Pitches());
        Assert.Equal([60], result.Score.Voice("Vocal").Pitches());
        Assert.All(doubled.Notes, note => Assert.Equal(80, note.Velocity));
    }

    [Fact]
    public void Doubling_follows_the_octave_shift_of_the_voice_it_copies()
    {
        var options = new ArrangementOptions
        {
            OctaveShifts = new Dictionary<string, int> { ["Vocal"] = 1 },
            Doubling = new DoublingOptions { Semitones = 12 },
        };

        var score = Arranger.Arrange(TwoVoices, options).Score;

        Assert.Equal([72], score.Voice("Vocal").Pitches());
        Assert.Equal([84], score.Voice("Vocal 8va").Pitches());
    }

    [Fact]
    public void Doubling_an_unknown_voice_is_reported()
    {
        var result = Arranger.Arrange(TwoVoices, new ArrangementOptions { Doubling = new DoublingOptions { VoiceId = "Choir" } });

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.UnknownVoice);
        Assert.Equal(2, result.Score.Voices.Count);
    }

    // ---- Groove ----------------------------------------------------------------------------------

    [Fact]
    public void Swing_delays_the_off_beat_eighths_and_leaves_the_beats_alone()
    {
        // Four eighth notes: the second and fourth sit on the off-beat.
        var score = ParseScore(Native("""
            V: Vocal
            C2C2C2C2C8|
            V: Ins
            Z1|
            """));

        var swung = Arranger.Arrange(score, new ArrangementOptions { Groove = new GrooveOptions { Swing = 1 } }).Score.Voice("Vocal");

        var eighth = Ppq / 2;
        var third = eighth / 3;
        Assert.Equal([0, eighth + third, 2 * eighth, (3 * eighth) + third, 4 * eighth], swung.Notes.Take(5).Select(n => n.StartTicks));

        // A delayed note is shortened by what it was delayed, so the next note keeps its place.
        Assert.Equal([eighth, eighth - third, eighth, eighth - third], swung.Notes.Take(4).Select(n => n.DurationTicks));
    }

    [Fact]
    public void Humanization_is_reproducible_and_stays_within_its_bounds()
    {
        var options = new ArrangementOptions
        {
            Drums = new DrumOptions(),
            Groove = new GrooveOptions { HumanizeTimingMs = 20, HumanizeVelocity = 10, Seed = 7 },
        };

        var first = Arranger.Arrange(Sample, options).Score;
        var second = Arranger.Arrange(Sample, options).Score;

        Assert.Equal(first.Voice("Vocal").Notes, second.Voice("Vocal").Notes);
        Assert.NotEqual(Sample.Voice("Vocal").Notes, first.Voice("Vocal").Notes);

        // 20 ms at 88 BPM and 480 ticks per quarter is a little over 14 ticks.
        var straight = Sample.Voice("Vocal").Notes;
        var moved = first.Voice("Vocal").Notes;
        Assert.All(moved.Zip(straight), pair => Assert.InRange(Math.Abs(pair.First.StartTicks - pair.Second.StartTicks), 0, 15));
        Assert.All(moved, note => Assert.InRange(note.Velocity!.Value, 96 - 10, 96 + 10));
    }

    [Fact]
    public void The_drums_can_stay_straight_under_a_swung_melody()
    {
        var options = new ArrangementOptions
        {
            Drums = new DrumOptions(),
            Groove = new GrooveOptions { Swing = 1, IncludeDrums = false },
        };

        var score = Arranger.Arrange(Sample, options).Score;

        var straight = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions() }).Score;
        Assert.Equal(straight.Voice("Drums").Notes, score.Voice("Drums").Notes);
        Assert.NotEqual(straight.Voice("Vocal").Notes, score.Voice("Vocal").Notes);
    }

    // ---- Mono and legato -------------------------------------------------------------------------

    [Fact]
    public void Mono_keeps_the_highest_of_two_notes_and_cuts_the_one_running_into_the_next()
    {
        // The YuE2 dialect has no chord stacks, so a score voice is monophonic already; a track that is not
        // reaches the arranger from a host that built it itself.
        var score = ScoreWith(
            new NoteEvent(0, 4 * Ppq, 60),
            new NoteEvent(0, Ppq, 64),
            new NoteEvent(2 * Ppq, Ppq, 62));

        var vocal = Arranger.Arrange(score, new ArrangementOptions { Mono = new MonoOptions() }).Score.Voice("Vocal");

        // 12 ms at 88 BPM and 480 ticks per quarter is about 8 ticks.
        Assert.Equal([64, 62], vocal.Pitches());
        Assert.Equal([Ppq, Ppq], vocal.Notes.Select(n => n.DurationTicks));

        var overlapping = Arranger.Arrange(
            ScoreWith(new NoteEvent(0, 4 * Ppq, 60), new NoteEvent(2 * Ppq, Ppq, 62)),
            new ArrangementOptions { Mono = new MonoOptions() }).Score.Voice("Vocal");
        Assert.Equal([(2 * Ppq) - 8, Ppq], overlapping.Notes.Select(n => n.DurationTicks));
    }

    /// <summary>A minimal score with one vocal track, for the cases the ABC dialect itself cannot express.</summary>
    private static ScoreDocument ScoreWith(params NoteEvent[] notes) => new(
        Ppq,
        Title: null,
        TempoBpm: 88,
        [new TimeSignatureChange(0, 4, 4)],
        [new KeySignatureChange(0, "C", 0, false)],
        [],
        [new VoiceTrack("Vocal", "Vocal", notes)],
        [],
        notes.Max(n => n.StartTicks + n.DurationTicks));

    [Fact]
    public void Legato_closes_the_holes_between_notes_but_keeps_the_gap()
    {
        var score = ParseScore(Native("""
            V: Vocal
            C2z2D2z2E8|
            V: Ins
            Z1|
            """));

        var legato = Arranger.Arrange(score, new ArrangementOptions { Mono = new MonoOptions { Legato = true } }).Score.Voice("Vocal");

        Assert.Equal([0, Ppq, 2 * Ppq], legato.Notes.Select(n => n.StartTicks));
        Assert.Equal([Ppq - 8, Ppq - 8], legato.Notes.Take(2).Select(n => n.DurationTicks));
    }

    [Fact]
    public void Very_short_notes_are_stretched_to_a_length_an_envelope_can_open_on()
    {
        var score = ParseScore(Native("""
            V: Vocal
            C1z15|C16|
            V: Ins
            Z2|
            """, unit: "1/64"));

        var vocal = Arranger.Arrange(score, new ArrangementOptions { Mono = new MonoOptions { MinimumLengthMs = 100 } }).Score.Voice("Vocal");

        // 100 ms at 88 BPM and 480 ticks per quarter is about 70 ticks; the note itself is 30.
        Assert.Equal(70, vocal.Notes[0].DurationTicks);
    }

    [Fact]
    public void Mono_runs_after_the_groove_so_its_gaps_survive_humanization()
    {
        var options = new ArrangementOptions
        {
            Groove = new GrooveOptions { Swing = 1, HumanizeTimingMs = 30, Seed = 3 },
            Mono = new MonoOptions(),
        };

        var vocal = Arranger.Arrange(Sample, options).Score.Voice("Vocal");

        Assert.All(
            vocal.Notes.Zip(vocal.Notes.Skip(1)),
            pair => Assert.True(pair.First.StartTicks + pair.First.DurationTicks <= pair.Second.StartTicks, "the groove moved notes into each other"));
    }

    [Fact]
    public void The_bass_can_be_left_polyphonic_while_the_melodies_are_not()
    {
        var options = new ArrangementOptions
        {
            Bass = new BassOptions(),
            Mono = new MonoOptions { IncludeBass = false },
        };

        var score = Arranger.Arrange(Sample, options).Score;

        var plain = Arranger.Arrange(Sample, new ArrangementOptions { Bass = new BassOptions() }).Score;
        Assert.Equal(plain.Voice("Bass").Notes, score.Voice("Bass").Notes);
    }


    // ---- Count-in --------------------------------------------------------------------------------

    [Fact]
    public void A_count_in_moves_the_whole_song_back_and_clicks_the_beats()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C16|"G"C16|
            V: Ins
            Z2|
            """));

        var result = Arranger.Arrange(score, new ArrangementOptions { CountIn = new CountInOptions() }).Score;

        // One bar of 4/4 in front of everything.
        Assert.Equal(Bar, result.CountInTicks);
        Assert.Equal(score.LengthTicks + Bar, result.LengthTicks);
        Assert.Equal(Bar, result.Voice("Vocal").Notes[0].StartTicks);
        Assert.Equal([Bar, 2 * Bar], result.Chords.Select(c => c.StartTicks));

        // Four clicks on the drum track, the downbeat played harder.
        var clicks = result.Voice("Drums");
        Assert.Equal(TrackKind.Drums, clicks.Kind);
        Assert.Equal([0, Ppq, 2 * Ppq, 3 * Ppq], clicks.Notes.Select(n => n.StartTicks));
        Assert.Equal([120, 100, 100, 100], clicks.Notes.Select(n => n.Velocity));
        Assert.All(clicks.Notes, n => Assert.Equal(GeneralMidiDrums.SideStick, n.NoteNumber));

        // The music itself is unchanged in length; only the document grew.
        Assert.Equal(score.DurationSeconds, result.MusicDurationSeconds, 6);
    }

    [Fact]
    public void A_count_in_keeps_the_opening_meter_and_key_in_place_and_moves_the_changes()
    {
        var score = ParseScore(Native("""
            V: Vocal
            C16|
            V: Ins
            Z1|
            V: Vocal
            K:Fm
            M:3/4
            C12|
            V: Ins
            K:Fm
            M:3/4
            Z1|
            """));
        Assert.Equal([0L, Bar], score.TimeSignatures.Select(t => t.StartTicks));

        var result = Arranger.Arrange(score, new ArrangementOptions { CountIn = new CountInOptions { Bars = 2 } }).Score;

        // The signature and key the song opens in govern the lead-in too, so those stay at tick 0.
        Assert.Equal([0L, 3 * Bar], result.TimeSignatures.Select(t => t.StartTicks));
        Assert.Equal([0L, 3 * Bar], result.KeySignatures.Select(k => k.StartTicks));
        Assert.Equal(2 * Bar, result.CountInTicks);
        Assert.Equal(8, result.Voice("Drums").Notes.Count);
    }

    [Fact]
    public void A_count_in_clicks_in_front_of_an_existing_drum_track()
    {
        var options = new ArrangementOptions { Drums = new DrumOptions(), CountIn = new CountInOptions() };

        var result = Arranger.Arrange(Sample, options).Score;

        var plain = Arranger.Arrange(Sample, new ArrangementOptions { Drums = new DrumOptions() }).Score;
        var drums = result.Voice("Drums");
        Assert.Equal(plain.Voice("Drums").Notes.Count + 4, drums.Notes.Count);
        Assert.All(drums.Notes.Take(4), n => Assert.Equal(GeneralMidiDrums.SideStick, n.NoteNumber));
        Assert.All(drums.Notes.Skip(4), n => Assert.True(n.StartTicks >= Bar, "a drum note landed in the count-in"));
    }

    [Fact]
    public void A_silent_count_in_adds_no_drum_track()
    {
        var options = new ArrangementOptions { CountIn = new CountInOptions { Click = false } };

        var result = Arranger.Arrange(TwoVoices, options).Score;

        Assert.DoesNotContain(result.Voices, v => v.Kind == TrackKind.Drums);
        Assert.Equal(Bar, result.CountInTicks);
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
                Chords = new ChordOptions { Inversion = ChordInversion.Closest },
                GuideTones = new GuideToneOptions { OctaveShift = 1 },
                Doubling = new DoublingOptions { Semitones = 12 },
                Groove = new GrooveOptions { Swing = 0.6, SwingUnit = SwingUnit.Sixteenths, HumanizeVelocity = 8 },
                Mono = new MonoOptions { Legato = true },
                CountIn = new CountInOptions { Bars = 2, Click = false },
            },
            FitTempo = new TempoFitOptions { AudioSeconds = 352.68, MaxDeviation = 0.02 },
        };

        var json = JsonSerializer.Serialize(options, YueToLogicJsonContext.Default.ConversionOptions);
        var restored = JsonSerializer.Deserialize(json, YueToLogicJsonContext.Default.ConversionOptions)!;

        Assert.Contains("\"pattern\": \"RootFifth\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inversion\": \"Closest\"", json, StringComparison.Ordinal);
        Assert.Equal(1, restored.Arrangement.OctaveShifts["Vocal"]);
        Assert.Equal(options.Arrangement.Bass, restored.Arrangement.Bass);
        Assert.Equal(options.Arrangement.Drums, restored.Arrangement.Drums);
        Assert.Equal(options.Arrangement.Chords, restored.Arrangement.Chords);
        Assert.Equal(options.Arrangement.GuideTones, restored.Arrangement.GuideTones);
        Assert.Equal(options.Arrangement.Doubling, restored.Arrangement.Doubling);
        Assert.Equal(options.Arrangement.Groove, restored.Arrangement.Groove);
        Assert.Equal(options.Arrangement.Mono, restored.Arrangement.Mono);
        Assert.Equal(options.Arrangement.CountIn, restored.Arrangement.CountIn);
        Assert.Equal(options.FitTempo, restored.FitTempo);
        Assert.Equal(-1, restored.Arrangement.DefaultOctaveShift);
    }
}
