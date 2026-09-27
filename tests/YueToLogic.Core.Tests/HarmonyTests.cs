using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Tests;

public class HarmonyTests
{
    [Theory]
    [InlineData("36", 36)]
    [InlineData("C1", 36)]
    [InlineData(" c1 ", 36)]
    [InlineData("F#1", 42)]
    [InlineData("Gb1", 42)]
    [InlineData("Bb0", 34)]
    [InlineData("C3", 60)]
    [InlineData("C-2", 0)]
    [InlineData("G8", 127)]
    public void Note_names_follow_logic_where_middle_c_is_c3(string text, int expected)
    {
        Assert.True(NoteNames.TryParse(text, out var note));
        Assert.Equal(expected, note);
        Assert.Equal(expected, NoteNames.TryParse(NoteNames.Name(expected), out var again) ? again : -1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("H1")]
    [InlineData("C")]
    [InlineData("128")]
    [InlineData("-1")]
    [InlineData("G#8")]
    [InlineData("kick")]
    public void What_is_not_a_note_is_refused(string text)
    {
        Assert.False(NoteNames.TryParse(text, out _));
    }

    [Fact]
    public void Notes_are_named_with_sharps_in_logic_octaves()
    {
        Assert.Equal("C1", NoteNames.Name(36));
        Assert.Equal("D#1", NoteNames.Name(39));
        Assert.Equal("A#1", NoteNames.Name(46));
        Assert.Equal("C-2", NoteNames.Name(0));
    }

    [Theory]
    [InlineData("C", 0, ChordQuality.Major, null)]
    [InlineData("Am", 9, ChordQuality.Minor, null)]
    [InlineData("Bdim", 11, ChordQuality.Diminished, null)]
    [InlineData("Dbaug", 1, ChordQuality.Augmented, null)]
    [InlineData("G7", 7, ChordQuality.Dominant7, null)]
    [InlineData("Gmaj7", 7, ChordQuality.Major7, null)]
    [InlineData("Am7", 9, ChordQuality.Minor7, null)]
    [InlineData("F#dim7", 6, ChordQuality.Diminished7, null)]
    [InlineData("Bm7b5", 11, ChordQuality.HalfDiminished7, null)]
    [InlineData("Dsus4", 2, ChordQuality.Suspended4, null)]
    [InlineData("Esus2", 4, ChordQuality.Suspended2, null)]
    [InlineData("F6", 5, ChordQuality.Major6, null)]
    [InlineData("Gm6/Bb", 7, ChordQuality.Minor6, 10)]
    [InlineData("A7sus4", 9, ChordQuality.Dominant7Suspended4, null)]
    [InlineData("Cm(maj7)", 0, ChordQuality.MinorMajor7, null)]
    [InlineData("F#m7/C#", 6, ChordQuality.Minor7, 1)]
    [InlineData("A7/E", 9, ChordQuality.Dominant7, 4)]
    [InlineData("Ebb", 2, ChordQuality.Major, null)]
    [InlineData("Cb", 11, ChordQuality.Major, null)]
    public void Native_vocabulary_is_parsed(string text, int root, ChordQuality quality, int? bass)
    {
        Assert.True(ChordSymbolParser.TryParse(text, out var chord));
        Assert.Equal(new ChordSymbol(root, quality, bass), chord);
    }

    [Theory]
    [InlineData("Cmaj9")]
    [InlineData("C13")]
    [InlineData("A7alt")]
    [InlineData("C:maj")]
    [InlineData("N.C.")]
    public void Symbols_outside_the_vocabulary_are_rejected(string text)
    {
        Assert.False(ChordSymbolParser.TryParse(text, out _));
    }

    [Fact]
    public void Root_only_fallback_keeps_the_root()
    {
        Assert.Equal(new ChordSymbol(3, ChordQuality.Major, null), ChordSymbolParser.ParseRootOnly("Ebmaj9"));
        Assert.Null(ChordSymbolParser.ParseRootOnly("N.C."));
    }

    [Fact]
    public void Voicing_places_the_root_in_octave_three_and_the_slash_bass_below()
    {
        Assert.True(ChordSymbolParser.TryParse("F#m7/C#", out var chord));

        Assert.Equal([37, 54, 57, 61, 64], ChordVoicing.GetNotes(chord));
    }

    [Fact]
    public void Every_quality_has_a_voicing_starting_on_the_root()
    {
        Assert.All(Enum.GetValues<ChordQuality>(), quality => Assert.Equal(0, ChordVoicing.GetIntervals(quality)[0]));
    }
}
