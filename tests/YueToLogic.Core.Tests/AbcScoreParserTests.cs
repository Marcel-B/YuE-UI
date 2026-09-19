using System.Globalization;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Diagnostics;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class AbcScoreParserTests
{
    [Fact]
    public void Official_sample_is_read_without_warnings()
    {
        var result = new AbcScoreParser().Parse(File.ReadAllText(SamplePath));

        Assert.True(result.Success);
        Assert.Empty(result.Warnings());

        var score = result.Score!;
        Assert.Equal(88, score.TempoBpm);
        Assert.Equal(8 * Bar, score.LengthTicks);
        Assert.Equal(56, score.Voice("Vocal").Notes.Count);
        Assert.Empty(score.Voice("Ins").Notes);
        Assert.Equal("Vocal Melody", score.Voice("Vocal").DisplayName);
        Assert.Equal([("verse", 0L), ("chorus", 4 * Bar)], score.Sections.Select(s => (s.Name, s.StartTicks)));
        Assert.Equal(["C", "G", "Am", "F", "C", "F", "G", "C"], score.Chords.Select(c => c.Text));
        Assert.All(score.Chords, chord => Assert.Equal(Bar, chord.DurationTicks));

        // "C"E2G2A2G2E2D2C4| starts the verse
        Assert.Equal([64, 67, 69, 67, 64, 62, 60], score.Voice("Vocal").Pitches()[..7]);
        Assert.Equal([240L, 240, 240, 240, 240, 240, 480], score.Voice("Vocal").Notes.Take(7).Select(n => n.DurationTicks));
    }

    [Theory]
    [InlineData("1/16", 120)]
    [InlineData("1/32", 60)]
    [InlineData("1/8", 240)]
    public void Durations_are_multiples_of_the_unit_length(string unit, long unitTicks)
    {
        int[] multipliers = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48];
        var lines = string.Join("\n", multipliers.Select(m => $"V: Vocal\nC{m}|"));

        var result = new AbcScoreParser().Parse(Native(lines, unit: unit));

        Assert.Equal(multipliers.Select(m => m * unitTicks), result.Score!.Voice("Vocal").Notes.Select(n => n.DurationTicks));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.NonNativeDuration);
    }

    [Fact]
    public void Octave_marks_follow_the_abc_convention()
    {
        var score = ParseScore(Native("V: Vocal\nC4c4c'4C,4|"));

        Assert.Equal([60, 72, 84, 48], score.Voice("Vocal").Pitches());
    }

    [Fact]
    public void Key_signature_alters_unmarked_notes()
    {
        var score = ParseScore(Native("V: Vocal\nF4C4G4c4|", key: "D"));

        Assert.Equal([66, 61, 67, 73], score.Voice("Vocal").Pitches());
    }

    [Fact]
    public void Accidental_applies_to_its_letter_in_every_octave_until_the_bar_line()
    {
        var score = ParseScore(Native("V: Vocal\n^F4f4F,4z4|F16|"));

        Assert.Equal([66, 78, 54, 65], score.Voice("Vocal").Pitches());
    }

    [Fact]
    public void Natural_cancels_the_key_signature_for_the_rest_of_the_bar()
    {
        var score = ParseScore(Native("V: Vocal\nF4=F4f4z4|F16|", key: "G"));

        Assert.Equal([66, 65, 77, 66], score.Voice("Vocal").Pitches());
    }

    [Fact]
    public void Tied_continuation_keeps_its_pitch_across_the_bar_line()
    {
        // Example from the YuE2 dialect reference: a five-quarter F-sharp, then a three-quarter F-natural.
        var score = ParseScore(Native("V: Vocal\n^F32-|F8F24|", unit: "1/32"));

        var notes = score.Voice("Vocal").Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal((66, 5L * Ppq), (notes[0].NoteNumber, notes[0].DurationTicks));
        Assert.Equal((65, 3L * Ppq), (notes[1].NoteNumber, notes[1].DurationTicks));
    }

    [Fact]
    public void Chord_change_inside_a_tied_note_does_not_retrigger_the_note()
    {
        var score = ParseScore(Native("V: Vocal\n\"C\"E16-\"Am7\"E16|", unit: "1/32"));

        var note = Assert.Single(score.Voice("Vocal").Notes);
        Assert.Equal(Bar, note.DurationTicks);
        Assert.Equal([(0L, "C"), (2L * Ppq, "Am7")], score.Chords.Select(c => (c.StartTicks, c.Text)));
    }

    [Fact]
    public void Multi_measure_rest_advances_the_voice_by_whole_bars()
    {
        var score = ParseScore(Native("""
            V: Vocal
            C16|D16|E16|F16|
            V: Ins
            Z4|
            V: Vocal
            G16|
            V: Ins
            c16|
            """));

        Assert.Equal(5 * Bar, score.LengthTicks);
        Assert.Equal(4 * Bar, Assert.Single(score.Voice("Ins").Notes).StartTicks);
        Assert.Equal(4 * Bar, score.Voice("Vocal").Notes[^1].StartTicks);
    }

    [Fact]
    public void Voices_of_a_group_start_at_the_same_tick_and_short_voices_are_padded()
    {
        var result = new AbcScoreParser().Parse(Native("""
            V: Vocal
            C16|D16|
            V: Ins
            E16|
            V: Vocal
            F16|
            V: Ins
            G16|
            """));

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.VoicesOutOfSync);
        var score = result.Score!;
        Assert.Equal(2 * Bar, score.Voice("Vocal").Notes[^1].StartTicks);
        Assert.Equal(2 * Bar, score.Voice("Ins").Notes[^1].StartTicks);
    }

    [Fact]
    public void Wrong_bar_length_is_reported_and_the_bar_grid_is_kept()
    {
        var result = new AbcScoreParser().Parse(Native("V: Vocal\nC8|D16|"));

        var warning = Assert.Single(result.Warnings());
        Assert.Equal(DiagnosticCodes.BarLengthMismatch, warning.Code);
        Assert.Equal(Bar, result.Score!.Voice("Vocal").Notes[1].StartTicks);
    }

    [Fact]
    public void Score_that_ends_inside_a_bar_is_reported_as_truncated()
    {
        // YuE stops writing when it reaches its token limit, e.g. "…|e2z" without a closing bar line.
        var result = new AbcScoreParser().Parse(Native("V: Vocal\nC16|D16|\nV: Ins\nZ|E4E2z"));

        var warning = Assert.Single(result.Warnings());
        Assert.Equal(DiagnosticCodes.ScoreTruncated, warning.Code);
        Assert.Contains("bar 2 of voice 'Ins' (1.75 of 4 quarter notes)", warning.Message, StringComparison.Ordinal);
        Assert.Equal(2 * Bar, result.Score!.LengthTicks);
    }

    [Fact]
    public void Missing_bar_line_inside_the_score_is_reported_as_such()
    {
        var result = new AbcScoreParser().Parse(Native("V: Vocal\nC8\nV: Ins\nZ|"));

        Assert.Contains(result.Warnings(), d => d.Code == DiagnosticCodes.MissingBarLine);
        Assert.DoesNotContain(result.Warnings(), d => d.Code == DiagnosticCodes.ScoreTruncated);
    }

    [Fact]
    public void Key_change_starts_a_new_key_signature_and_resets_accidentals()
    {
        var score = ParseScore(Native("""
            V: Vocal
            F16|
            V: Ins
            Z|
            V: Vocal
            K:G
            F16|
            V: Ins
            K:G
            Z|
            V: Vocal
            [K:C]F16|
            V: Ins
            Z|
            """));

        Assert.Equal([("C", 0L), ("G", Bar), ("C", 2 * Bar)], score.KeySignatures.Select(k => (k.Key, k.StartTicks)));
        Assert.Equal([65, 66, 65], score.Voice("Vocal").Pitches());
    }

    [Fact]
    public void Meter_change_is_reflected_in_bar_positions()
    {
        var score = ParseScore(Native("""
            V: Vocal
            C16|
            V: Ins
            Z|
            V: Vocal
            M:3/4
            D12|E12|
            V: Ins
            M:3/4
            Z2|
            """));

        Assert.Equal([(4, 4), (3, 4)], score.TimeSignatures.Select(t => (t.Numerator, t.Denominator)));
        Assert.Equal(Bar + 2 * 3 * Ppq, score.LengthTicks);
        Assert.Equal(new(3, 1), score.GetBarPosition(Bar + 3 * Ppq));
        Assert.Equal(new(2, 2.5), score.GetBarPosition(Bar + Ppq + Ppq / 2));
    }

    [Fact]
    public void Chords_end_at_the_next_section()
    {
        var score = ParseScore(Native("""
            % verse
            V: Vocal
            "C"C16|z16|
            V: Ins
            Z2|
            % chorus
            V: Vocal
            z16|"G"G16|
            V: Ins
            Z2|
            """));

        Assert.Equal([(0L, 2 * Bar), (3 * Bar, Bar)], score.Chords.Select(c => (c.StartTicks, c.DurationTicks)));
    }

    [Fact]
    public void Unsupported_notation_is_reported_and_skipped()
    {
        var result = new AbcScoreParser().Parse(Native("V: Vocal\n(3C4C4C4 {g}D4!trill!E4|"));

        Assert.True(result.Success);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == DiagnosticCodes.UnsupportedNotation));
        Assert.Equal([60, 60, 60, 62, 64], result.Score!.Voice("Vocal").Pitches());
        Assert.All(result.Diagnostics.Where(d => d.Code == DiagnosticCodes.UnsupportedNotation), d => Assert.Equal(10, d.Line));
    }

    [Fact]
    public void Missing_header_fields_fall_back_to_defaults()
    {
        var result = new AbcScoreParser().Parse("C2D2E2F2|");

        Assert.True(result.Success);
        Assert.Equal(120, result.Score!.TempoBpm);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MissingHeaderField);
        Assert.Equal([60, 62, 64, 65], result.Score.Voice("Melody").Pitches());
    }

    [Fact]
    public void Score_without_music_fails()
    {
        var result = new AbcScoreParser().Parse(Native(string.Empty));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d is { Code: DiagnosticCodes.NoMusic, Severity: DiagnosticSeverity.Error });
    }

    [Fact]
    public void Unit_length_that_does_not_fit_the_tick_grid_fails()
    {
        var result = new AbcScoreParser().Parse(Native("V: Vocal\nC|", unit: "1/1024"));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.TickResolution);
        Assert.True(new AbcScoreParser().Parse(Native("V: Vocal\nC|", unit: "1/1024"), new AbcParseOptions { TicksPerQuarterNote = 1024 }).Success);
    }

    [Fact]
    public void Parsing_does_not_depend_on_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var score = ParseScore(Native("V: Vocal\nC16|", tempo: "1/4=90.5"));
            Assert.Equal(90.5, score.TempoBpm);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("1/8=120", 60)]
    [InlineData("100", 100)]
    [InlineData("\"Allegro\" 1/4=132", 132)]
    public void Tempo_forms_are_converted_to_quarter_note_bpm(string tempo, double expected)
    {
        Assert.Equal(expected, ParseScore(Native("V: Vocal\nC16|", tempo: tempo)).TempoBpm);
    }
}
