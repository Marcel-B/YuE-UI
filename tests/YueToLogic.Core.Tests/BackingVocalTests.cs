using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Midi;
using YueToLogic.Core.Model;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class BackingVocalTests
{
    private static readonly ScoreArranger Arranger = new();

    private static ArrangementResult Arrange(ScoreDocument score, HarmonyOptions harmony) =>
        Arranger.Arrange(score, new ArrangementOptions { Harmony = harmony });

    private static int[] Part(ArrangementResult result, HarmonyPart part) =>
        result.Score.Voice(HarmonyGenerator.TrackId(part)).Pitches();

    [Fact]
    public void Parallel_parts_move_by_scale_steps_and_land_on_the_chord()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C4E4G4c4|
            """));

        var result = Arrange(score, new HarmonyOptions { Parts = [HarmonyPart.ThirdAbove, HarmonyPart.ThirdBelow, HarmonyPart.SixthBelow] });

        // Above G the third would be B, outside C major, so the part sings the C; below c likewise G instead of A.
        Assert.Equal([64, 67, 72, 76], Part(result, HarmonyPart.ThirdAbove));
        Assert.Equal([55, 60, 64, 67], Part(result, HarmonyPart.ThirdBelow));
        Assert.Equal([52, 55, 60, 64], Part(result, HarmonyPart.SixthBelow));
        Assert.All(result.Score.Voices.Where(v => v.Kind == TrackKind.Harmony), v => Assert.Equal(4, v.Notes.Count));
    }

    [Fact]
    public void A_passing_note_is_followed_in_parallel()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C4D4E8|
            """));

        var result = Arrange(score, new HarmonyOptions { Parts = [HarmonyPart.ThirdAbove] });

        Assert.Equal([64, 65, 67], Part(result, HarmonyPart.ThirdAbove));
    }

    [Fact]
    public void The_raised_leading_tone_of_minor_finds_its_chord()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "E"^G8B8|"Am"A16|
            """, key: "Am"));

        var result = Arrange(score, new HarmonyOptions { Parts = [HarmonyPart.ThirdBelow] });

        Assert.Equal([64, 68, 64], Part(result, HarmonyPart.ThirdBelow));
    }

    [Fact]
    public void Parts_keep_the_rhythm_of_the_melody_and_come_after_it()
    {
        var result = Arrange(ParseScore(File.ReadAllText(SamplePath)), new HarmonyOptions { Parts = [HarmonyPart.ThirdAbove, HarmonyPart.Alto] });

        var vocal = result.Score.Voice("Vocal").Notes;
        foreach (var part in new[] { HarmonyPart.ThirdAbove, HarmonyPart.Alto })
        {
            var notes = result.Score.Voice(HarmonyGenerator.TrackId(part)).Notes;
            Assert.Equal(vocal.Select(n => (n.StartTicks, n.DurationTicks)), notes.Select(n => (n.StartTicks, n.DurationTicks)));
        }

        Assert.Equal(["Vocal", "Ins", "Harmony 3rd up", "Harmony Alto"], result.Score.Voices.Select(v => v.Id));
    }

    [Fact]
    public void Choir_parts_stay_below_each_other_and_the_bass_sings_the_root()
    {
        var score = ParseScore(File.ReadAllText(SamplePath));

        var result = Arrange(score, new HarmonyOptions { Parts = [HarmonyPart.Alto, HarmonyPart.Tenor, HarmonyPart.Bass] });

        var soprano = result.Score.Voice("Vocal").Notes;
        var alto = Part(result, HarmonyPart.Alto);
        var tenor = Part(result, HarmonyPart.Tenor);
        var bass = Part(result, HarmonyPart.Bass);
        for (var i = 0; i < soprano.Count; i++)
        {
            Assert.True(soprano[i].NoteNumber > alto[i] && alto[i] > tenor[i] && tenor[i] > bass[i], $"note {i}: {soprano[i].NoteNumber} {alto[i]} {tenor[i]} {bass[i]}");
            var chord = score.Chords.Last(c => c.StartTicks <= soprano[i].StartTicks).Symbol!;
            var tones = ChordVoicing.GetIntervals(chord.Quality).Select(interval => (chord.RootPitchClass + interval) % 12).ToHashSet();
            if (tones.Contains(soprano[i].NoteNumber % 12))
            {
                Assert.Equal(chord.RootPitchClass, bass[i] % 12);
                Assert.Contains(alto[i] % 12, tones);
                Assert.Contains(tenor[i] % 12, tones);
            }
        }
    }

    [Fact]
    public void Only_the_chosen_sections_get_backing_vocals()
    {
        var score = ParseScore(File.ReadAllText(SamplePath));
        var chorus = score.Sections.Single(s => s.Name == "chorus").StartTicks;

        var result = Arrange(score, new HarmonyOptions { Parts = [HarmonyPart.ThirdAbove], Sections = ["Chorus", "Refrain"] });

        var notes = result.Score.Voice("Harmony 3rd up").Notes;
        Assert.Equal(score.Voice("Vocal").Notes.Count(n => n.StartTicks >= chorus), notes.Count);
        Assert.All(notes, n => Assert.True(n.StartTicks >= chorus));
    }

    [Fact]
    public void A_section_the_score_does_not_have_leaves_the_parts_empty_with_a_warning()
    {
        var result = Arrange(ParseScore(File.ReadAllText(SamplePath)), new HarmonyOptions { Sections = ["bridge"] });

        Assert.Empty(result.Score.Voice("Harmony 3rd up").Notes);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.HarmonySections && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Without_a_fitting_key_signature_the_key_comes_from_the_melody()
    {
        // D major written without a signature, as a MIDI file without key events reads.
        var score = ParseScore(Native("""
            V: Vocal
            D4^F4A4d4|^c4B4A8|G4^F4E4^F4|D16|
            """));

        var result = Arrange(score, new HarmonyOptions());

        Assert.Equal(66, Part(result, HarmonyPart.ThirdAbove)[0]);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.HarmonyKey && d.Message.Contains(" D,", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.HarmonyChords);
    }

    [Fact]
    public void A_chosen_key_wins_over_the_score()
    {
        var score = ParseScore(Native("""
            V: Vocal
            "C"C4D4E8|
            """));

        var inC = Arrange(score, new HarmonyOptions());
        var inD = Arrange(score, new HarmonyOptions { Key = "D" });

        // The passing D is followed in the scale: F in C major, F# in D major.
        Assert.Equal(65, Part(inC, HarmonyPart.ThirdAbove)[1]);
        Assert.Equal(66, Part(inD, HarmonyPart.ThirdAbove)[1]);
    }

    [Fact]
    public void Guessed_chords_follow_the_melody()
    {
        var score = ParseScore(Native("""
            V: Vocal
            C4E4G8|F4A4c8|G4B4d8|C16|
            """));

        var chords = ChordGuesser.Guess(score, score.Voice("Vocal").Notes, new ScaleKey(0, false));

        Assert.Equal(["C", "F", "G", "C"], chords.Select(c => c.Text));
        Assert.Equal(4 * Bar, chords.Sum(c => c.DurationTicks));
    }

    [Fact]
    public void The_parts_reach_the_midi_file_and_the_way_back_leaves_them_out()
    {
        var options = new ConversionOptions { Arrangement = new ArrangementOptions { Harmony = new HarmonyOptions { Parts = [HarmonyPart.ThirdAbove, HarmonyPart.Tenor] } } };
        var plain = new ScoreConverter().Convert(File.ReadAllText(SamplePath));
        var harmonized = new ScoreConverter().Convert(File.ReadAllText(SamplePath), options);
        Assert.True(harmonized.Success);

        var midi = Melanchall.DryWetMidi.Core.MidiFile.Read(new MemoryStream(harmonized.Midi!));
        var names = midi.Chunks.OfType<Melanchall.DryWetMidi.Core.TrackChunk>()
            .Select(chunk => chunk.Events.OfType<Melanchall.DryWetMidi.Core.SequenceTrackNameEvent>().FirstOrDefault()?.Text)
            .ToList();
        Assert.Contains("Harmony 3rd up", names);
        Assert.Contains("Harmony Tenor", names);

        var back = new MidiToAbcConverter().Convert(harmonized.Midi!);
        var plainBack = new MidiToAbcConverter().Convert(plain.Midi!);
        Assert.Equal(plainBack.Abc, back.Abc);
    }

    [Fact]
    public void The_drone_sings_the_tonic_in_the_melody_rhythm()
    {
        var score = DronePhrases();

        var result = Arrange(score, new HarmonyOptions { Parts = [HarmonyPart.Drone] });

        var vocal = score.Voice("Vocal").Notes;
        var drone = result.Score.Voice("Harmony Drone").Notes;
        Assert.Equal(vocal.Select(n => (n.StartTicks, n.DurationTicks)), drone.Select(n => (n.StartTicks, n.DurationTicks)));
        // The G at least a minor third below each phrase's lowest note: under the G of the first phrase that is 55.
        Assert.All(drone, n => Assert.Equal(55, n.NoteNumber));
    }

    [Fact]
    public void The_held_drone_holds_the_tonic_under_each_phrase()
    {
        var result = Arrange(DronePhrases(), new HarmonyOptions { Parts = [HarmonyPart.DroneHeld] });

        var drone = result.Score.Voice("Harmony Drone held").Notes;
        Assert.Equal([(0L, 6L * Ppq), (2 * Bar, 2 * Bar)], drone.Select(n => (n.StartTicks, n.DurationTicks)));
        Assert.Equal([55, 55], drone.Select(n => n.NoteNumber));
    }

    /// <summary>Two phrases in G major, parted by a rest of a half bar.</summary>
    private static ScoreDocument DronePhrases() => ParseScore(Native("""
        V: Vocal
        "G"G4A4B4d4|B4A4z8|"D"A4B4c4d4|B16|
        """, key: "G"));

    [Theory]
    [InlineData("H#")]
    [InlineData("Dorian")]
    public void An_unknown_key_is_refused(string key)
    {
        var options = new ConversionOptions { Arrangement = new ArrangementOptions { Harmony = new HarmonyOptions { Key = key } } };

        Assert.Contains(ConversionOptionsValidator.Validate(options), d => d.Code == DiagnosticCodes.InvalidOption);
    }
}
