using YueToLogic.Core.Abc;
using YueToLogic.Core.Model;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class AbcScoreWriterTests
{
    [Fact]
    public void The_official_sample_is_written_again_character_for_character()
    {
        var original = File.ReadAllText(SamplePath).ReplaceLineEndings("\n");

        var written = new AbcScoreWriter().Write(ParseScore(original));

        Assert.Equal(original.TrimEnd('\n'), written.TrimEnd('\n'));
    }

    [Fact]
    public void A_long_YuE2_score_is_written_again_character_for_character_up_to_where_YuE_broke_it_off()
    {
        // YuE stopped writing this score inside a bar; the parser completes that bar with rests, so only its
        // last line reads differently.
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logic-template-song.abc")).ReplaceLineEndings("\n");
        var lastLine = original.TrimEnd('\n').LastIndexOf('\n');

        var written = new AbcScoreWriter().Write(ParseScore(original));

        Assert.Equal(original[..lastLine], written[..lastLine]);
        Assert.Equal("B,,8F2E4B,2|Z3|", written[(lastLine + 1)..].TrimEnd('\n'));
    }

    [Fact]
    public void Accidentals_hold_for_the_letter_in_every_octave_until_the_bar_line()
    {
        // F#4, F#5, F4, then F#4 in the next bar: the second needs no mark, the third a natural, the fourth a sharp again.
        var score = Score([Note(0, 4, 66), Note(4, 4, 78), Note(8, 8, 65), Note(16, 16, 66)]);

        var abc = new AbcScoreWriter().Write(score);

        Assert.Contains("^F4f4=F8|^F16|", abc);
        Assert.Equal(VocalPitches(score), VocalPitches(ParseScore(abc)));
    }

    [Fact]
    public void Flat_keys_spell_with_flats_and_sharp_keys_with_sharps()
    {
        int[] chromatic = [60, 61, 63, 66, 68, 70];
        var notes = chromatic.Select((pitch, i) => Note(i * 2, 2, pitch)).ToArray();

        var inF = new AbcScoreWriter().Write(Score(notes, key: new KeySignatureChange(0, "F", -1, false)));
        var inD = new AbcScoreWriter().Write(Score(notes, key: new KeySignatureChange(0, "D", 2, false)));

        Assert.Contains("K:F", inF);
        Assert.Contains("C2_D2_E2_G2_A2B2", inF);
        Assert.Contains("C2^C2^D2F2^G2^A2", inD.Replace("=C2", "C2", StringComparison.Ordinal));
        Assert.Equal(chromatic, ParseScore(inF).Voice("Vocal").Pitches());
        Assert.Equal(chromatic, ParseScore(inD).Voice("Vocal").Pitches());
    }

    [Fact]
    public void Notes_across_bar_lines_and_odd_lengths_are_tied_from_native_lengths()
    {
        // Five sixteenths, then a note of ten from the last quarter of bar 1 into bar 2.
        var score = Score([Note(0, 5, 64), Note(12, 10, 67)]);

        var abc = new AbcScoreWriter().Write(score);

        Assert.Contains("E4-Ez6zG4-|G6z8z2|", abc);
        var parsed = ParseScore(abc).Voice("Vocal").Notes;
        Assert.Equal([(0L, 5L), (12L, 10L)], parsed.Select(n => (n.StartTicks / (Ppq / 4), n.DurationTicks / (Ppq / 4))));
    }

    [Fact]
    public void A_chord_change_inside_a_note_splits_it_with_a_tie()
    {
        var score = Score(
            [Note(0, 16, 64)],
            chords: [Chord(0, 8, "C"), Chord(8, 8, "Am")]);

        var abc = new AbcScoreWriter().Write(score);

        Assert.Contains("\"C\"E8-\"Am\"E8|", abc);
        var parsed = ParseScore(abc);
        Assert.Equal(["C", "Am"], parsed.Chords.Select(c => c.Text));
        Assert.Single(parsed.Voice("Vocal").Notes);
    }

    [Fact]
    public void Meter_and_key_changes_open_a_group_and_are_read_back()
    {
        var score = Score(
            [Note(0, 16, 60), Note(16, 12, 62), Note(28, 12, 64)],
            meters: [new TimeSignatureChange(0, 4, 4), new TimeSignatureChange(16 * Unit, 3, 4)],
            keys: [new KeySignatureChange(0, "C", 0, false), new KeySignatureChange(16 * Unit, "G", 1, false)]);

        var parsed = ParseScore(new AbcScoreWriter().Write(score));

        Assert.Equal([(0L, 4, 4), (16L * Unit, 3, 4)], parsed.TimeSignatures.Select(t => (t.StartTicks, t.Numerator, t.Denominator)));
        Assert.Equal(["C", "G"], parsed.KeySignatures.Select(k => k.Key));
        Assert.Equal([60, 62, 64], parsed.Voice("Vocal").Pitches());
    }

    private const int Unit = Ppq / 4;

    private static NoteEvent Note(long start, long length, int pitch) => new(start * Unit, length * Unit, pitch);

    private static ChordEvent Chord(long start, long length, string text) =>
        new(start * Unit, length * Unit, text, Harmony.ChordSymbolParser.TryParse(text, out var symbol) ? symbol : null);

    private static int[] VocalPitches(ScoreDocument score) => score.Voice("Vocal").Pitches();

    private static ScoreDocument Score(
        NoteEvent[] vocal,
        ChordEvent[]? chords = null,
        KeySignatureChange? key = null,
        TimeSignatureChange[]? meters = null,
        KeySignatureChange[]? keys = null)
    {
        var length = vocal.Max(n => n.StartTicks + n.DurationTicks);
        return new ScoreDocument(
            Ppq,
            null,
            100,
            meters ?? [new TimeSignatureChange(0, 4, 4)],
            keys ?? [key ?? new KeySignatureChange(0, "C", 0, false)],
            [],
            [new VoiceTrack("Vocal", "Vocal", vocal), new VoiceTrack("Ins", "Ins", [])],
            chords ?? [],
            length);
    }
}
