using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class MidiToAbcConverterTests
{
    private static string TemplateSong => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logic-template-song.abc"));

    public static TheoryData<string> Arrangements => new()
    {
        "as written",
        "arpeggio",
        "arpeggio up and down, closest inversion",
        "eighths, first inversion",
        "off-beat stabs",
        "full band with count-in",
        "humanized",
    };

    [Theory]
    [MemberData(nameof(Arrangements))]
    public void A_converted_score_comes_back_with_its_melodies_chords_and_sections(string arrangement)
    {
        foreach (var abc in new[] { File.ReadAllText(SamplePath), TemplateSong })
        {
            var original = ParseScore(abc);
            var midi = new ScoreConverter().Convert(abc, new ConversionOptions { Arrangement = Arrange(arrangement) }).Midi!;

            var result = new MidiToAbcConverter().Convert(midi);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var back = ParseScore(result.Abc!);
            Assert.Equal(Notes(original.Voice("Vocal")), Notes(back.Voice("Vocal")));
            Assert.Equal(Notes(original.Voice("Ins")), Notes(back.Voice("Ins")));
            Assert.Equal(ChordChanges(original), ChordChanges(back));
            Assert.Equal(original.Sections.Select(s => (s.StartTicks, s.Name)), back.Sections.Select(s => (s.StartTicks, s.Name)));
            Assert.Equal(original.TempoBpm, back.TempoBpm);
            Assert.Equal(original.KeySignatures.Select(k => k.Key), back.KeySignatures.Select(k => k.Key));
        }
    }

    [Fact]
    public void The_official_sample_comes_back_character_for_character()
    {
        var abc = File.ReadAllText(SamplePath).ReplaceLineEndings("\n");
        var midi = new ScoreConverter().Convert(abc).Midi!;

        var result = new MidiToAbcConverter().Convert(midi);

        Assert.Equal(abc.TrimEnd('\n'), result.Abc!.TrimEnd('\n'));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
    }

    [Fact]
    public void Tracks_are_recognized_by_the_names_Logic_gives_them()
    {
        var midi = Midi(
            Track("Vocal · Prophet", 1, (0, 480, 64)),
            Track("Inst.", 2, (0, 480, 72)),
            Track("Chords · Juno", 3, (0, 1920, 48), (0, 1920, 52), (0, 1920, 55)),
            Track("Bass · Mother32", 4, (0, 480, 36)),
            Track("Vocal 8vb", 5, (0, 480, 52)),
            Track("Kit", 10, (0, 120, 36)));

        var result = new MidiToAbcConverter().Convert(midi);

        Assert.Equal(
            [MidiTrackRole.Vocal, MidiTrackRole.Ins, MidiTrackRole.Chords, MidiTrackRole.Ignore, MidiTrackRole.Ignore, MidiTrackRole.Ignore],
            result.Tracks.Select(t => t.Role));
        Assert.Contains("\"C\"E4z12|", result.Abc);
    }

    [Fact]
    public void Unnamed_tracks_become_chords_by_their_sound_and_melodies_by_their_order()
    {
        var midi = Midi(
            Track("Track A", 1, (0, 480, 64)),
            Track("Track B", 2, (0, 1920, 50), (0, 1920, 53), (0, 1920, 57)),
            Track("Track C", 3, (0, 480, 72)));

        var result = new MidiToAbcConverter().Convert(midi);

        Assert.Equal([MidiTrackRole.Vocal, MidiTrackRole.Chords, MidiTrackRole.Ins], result.Tracks.Select(t => t.Role));
        Assert.True(result.Tracks[1].Polyphonic);
        Assert.Contains("\"Dm\"", result.Abc);
    }

    [Fact]
    public void Chosen_roles_override_the_names()
    {
        var midi = Midi(Track("Vocal", 1, (0, 480, 64)), Track("Bass", 2, (0, 480, 40)));

        var result = new MidiToAbcConverter().Convert(midi, new MidiToAbcOptions
        {
            TrackRoles = new Dictionary<int, MidiTrackRole> { [0] = MidiTrackRole.Ignore, [1] = MidiTrackRole.Ins },
        });

        var back = ParseScore(result.Abc!);
        Assert.Empty(back.Voice("Vocal").Notes);
        Assert.Equal([40], back.Voice("Ins").Pitches());
    }

    [Fact]
    public void Overlapping_notes_of_a_voice_are_made_one_at_a_time_and_reported()
    {
        // Two notes struck together, and one held into the next.
        var midi = Midi(Track("Vocal", 1, (0, 480, 64), (0, 480, 60), (480, 1440, 67), (960, 480, 65)));

        var result = new MidiToAbcConverter().Convert(midi);

        Assert.Equal([64, 67, 65], ParseScore(result.Abc!).Voice("Vocal").Pitches());
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MidiPolyphony);
    }

    [Fact]
    public void Notes_off_the_grid_move_to_the_nearest_sixteenth()
    {
        var midi = Midi(Track("Vocal", 1, (7, 470, 64), (478, 250, 65)));

        var result = new MidiToAbcConverter().Convert(midi);

        Assert.Contains("E4F2z8z2|", result.Abc);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MidiQuantized);
    }

    [Fact]
    public void Bars_to_leave_out_can_be_given_and_a_silent_start_is_left_out_by_default()
    {
        var midi = Midi(Track("Vocal", 1, (2 * 1920, 480, 64)));

        var automatic = new MidiToAbcConverter().Convert(midi);
        var kept = new MidiToAbcConverter().Convert(midi, new MidiToAbcOptions { SkipBars = 0 });

        Assert.Contains("E4z12|", automatic.Abc);
        Assert.Contains(automatic.Diagnostics, d => d.Code == DiagnosticCodes.MidiBarsSkipped);
        Assert.Contains("Z2|E4z12|", kept.Abc);
    }

    [Fact]
    public void Tempo_is_rounded_to_whole_bpm_and_changes_are_reported()
    {
        var midi = Midi(Track("Vocal", 1, (0, 480, 64)));
        var file = MidiFile.Read(new MemoryStream(midi));
        var conductor = new TrackChunk(
            new SetTempoEvent((long)Math.Round(60_000_000 / 87.63)),
            new SetTempoEvent(500_000) { DeltaTime = 1920 });
        file.Chunks[0] = conductor;

        var result = new MidiToAbcConverter().Convert(Bytes(file));

        Assert.Contains("Q:1/4=88", result.Abc);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MidiTempo && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_file_that_is_no_midi_file_is_refused()
    {
        var result = new MidiToAbcConverter().Convert("X:1\nK:C\n"u8.ToArray());

        Assert.False(result.Success);
        Assert.Equal(DiagnosticCodes.MidiUnreadable, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Without_notes_to_write_conversion_fails_but_lists_the_tracks()
    {
        var midi = Midi(Track("Drums", 10, (0, 120, 36)));

        var result = new MidiToAbcConverter().Convert(midi);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.MidiNoNotes);
        Assert.Equal("Drums", Assert.Single(result.Tracks).Name);
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        var result = new MidiToAbcConverter().Convert(Midi(), new MidiToAbcOptions { SkipBars = -1 });

        Assert.Equal(DiagnosticCodes.InvalidOption, Assert.Single(result.Diagnostics).Code);
    }

    // ---- Chord recognition ------------------------------------------------------------------------

    [Fact]
    public void Every_chord_of_the_vocabulary_is_recognized_from_its_voicing()
    {
        foreach (var quality in Enum.GetValues<ChordQuality>())
        {
            for (var root = 0; root < 12; root++)
            {
                var chord = new ChordSymbol(root, quality, null);
                Assert.Equal(chord, ChordRecognizer.Recognize(ChordVoicing.GetNotes(chord))?.Symbol);
            }
        }
    }

    [Fact]
    public void Every_triad_over_any_bass_is_recognized_from_its_voicing()
    {
        ChordQuality[] triads = [ChordQuality.Major, ChordQuality.Minor, ChordQuality.Diminished, ChordQuality.Suspended4, ChordQuality.Suspended2];
        foreach (var quality in triads)
        {
            for (var root = 0; root < 12; root++)
            {
                for (var bass = 0; bass < 12; bass++)
                {
                    if (bass == root)
                    {
                        continue;
                    }

                    var chord = new ChordSymbol(root, quality, bass);
                    var notes = ChordVoicing.GetNotes(chord);
                    var recognized = ChordRecognizer.Recognize(notes)!.Symbol;

                    // Some are another chord by name: C/A is Am7. Bass and sound survive in any case, and
                    // they are what YuE2 hears.
                    Assert.Equal(Sound(chord), Sound(recognized));
                }
            }
        }
    }

    [Theory]
    [InlineData(new[] { 52, 55, 60 }, "C")]            // first inversion inside an octave
    [InlineData(new[] { 40, 48, 52, 55 }, "C/E")]      // the bass set apart below the chord
    [InlineData(new[] { 43, 57, 60, 64 }, "Am/G")]
    [InlineData(new[] { 47, 48, 52, 55 }, "C/B")]      // a seventh at the bottom
    [InlineData(new[] { 57, 60, 64, 67 }, "Am7")]
    [InlineData(new[] { 48, 52, 55, 57 }, "C6")]
    [InlineData(new[] { 36, 48, 52, 55 }, "C")]        // a doubled root is no slash chord
    [InlineData(new[] { 46, 48, 51, 55 }, "Cm/Bb")]    // not Eb6, whose bass would be Eb
    [InlineData(new[] { 46, 55, 58, 62, 64 }, "Gm6/Bb")] // not Em7b5/Bb: the chord is stacked from its root
    [InlineData(new[] { 48, 55 }, "C")]                // a bare fifth, the best guess
    public void Chords_are_named_as_they_are_voiced(int[] notes, string expected)
    {
        Assert.Equal(expected, ChordSymbolParser.Format(ChordRecognizer.Recognize(notes)!.Symbol));
    }

    [Theory]
    [InlineData(0, "C#m7/G#")]
    [InlineData(2, "C#m7/G#")]
    [InlineData(-3, "Dbm7/Ab")]
    public void Chord_names_are_spelled_for_the_key(int sharps, string expected)
    {
        Assert.Equal(expected, ChordSymbolParser.Format(new ChordSymbol(1, ChordQuality.Minor7, 8), sharps));
        Assert.True(ChordSymbolParser.TryParse(expected, out var parsed));
        Assert.Equal(new ChordSymbol(1, ChordQuality.Minor7, 8), parsed);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static ArrangementOptions Arrange(string arrangement) => arrangement switch
    {
        "as written" => new ArrangementOptions(),
        "arpeggio" => new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.ArpeggioUp } },
        "arpeggio up and down, closest inversion" => new ArrangementOptions
        {
            Chords = new ChordOptions { Pattern = ChordPattern.ArpeggioUpDown, Inversion = ChordInversion.Closest },
        },
        "eighths, first inversion" => new ArrangementOptions
        {
            Chords = new ChordOptions { Pattern = ChordPattern.Eighths, Inversion = ChordInversion.First },
        },
        "off-beat stabs" => new ArrangementOptions { Chords = new ChordOptions { Pattern = ChordPattern.Offbeat } },
        "full band with count-in" => new ArrangementOptions
        {
            Chords = new ChordOptions { Pattern = ChordPattern.Sixteenths, Inversion = ChordInversion.Closest },
            Bass = new BassOptions(),
            Drums = new DrumOptions(),
            GuideTones = new GuideToneOptions(),
            Doubling = new DoublingOptions(),
            CountIn = new CountInOptions { Bars = 2 },
        },
        "humanized" => new ArrangementOptions
        {
            Chords = new ChordOptions { Pattern = ChordPattern.Eighths },
            Groove = new GrooveOptions { HumanizeTimingMs = 20, HumanizeVelocity = 10, IncludeDrums = true },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(arrangement), arrangement, null),
    };

    private static (long, long, int)[] Notes(VoiceTrack voice) =>
        [.. voice.Notes.Select(n => (n.StartTicks, n.DurationTicks, n.NoteNumber))];

    /// <summary>Where the chord changes and to what; repeating a chord at a bar or section start changes nothing.</summary>
    private static (long, string)[] ChordChanges(ScoreDocument score) =>
        [.. score.Chords.Where((c, i) => i == 0 || c.Text != score.Chords[i - 1].Text).Select(c => (c.StartTicks, c.Text))];

    /// <summary>The bass and the pitch classes of a chord, e.g. "4: 0 4 7" for C/E.</summary>
    private static string Sound(ChordSymbol chord)
    {
        var bass = chord.BassPitchClass ?? chord.RootPitchClass;
        var tones = ChordVoicing.GetIntervals(chord.Quality).Select(i => (chord.RootPitchClass + i) % 12).Append(bass).Distinct().Order();
        return $"{bass}: {string.Join(' ', tones)}";
    }

    private static TrackChunk Track(string name, int channel, params (long Start, long Length, int Pitch)[] notes)
    {
        var events = new List<(long Tick, MidiEvent Event)> { (0, new SequenceTrackNameEvent(name)) };
        var midiChannel = (FourBitNumber)(byte)(channel - 1);
        foreach (var (start, length, pitch) in notes)
        {
            events.Add((start, new NoteOnEvent((SevenBitNumber)(byte)pitch, (SevenBitNumber)(byte)90) { Channel = midiChannel }));
            events.Add((start + length, new NoteOffEvent((SevenBitNumber)(byte)pitch, SevenBitNumber.MinValue) { Channel = midiChannel }));
        }

        var chunk = new TrackChunk();
        var previous = 0L;
        foreach (var (tick, midiEvent) in events.OrderBy(e => e.Tick).ThenBy(e => e.Event is NoteOnEvent ? 1 : 0))
        {
            midiEvent.DeltaTime = tick - previous;
            previous = tick;
            chunk.Events.Add(midiEvent);
        }

        return chunk;
    }

    /// <summary>A type 1 file as a DAW writes it: a conductor track, then the parts.</summary>
    private static byte[] Midi(params TrackChunk[] tracks) =>
        Bytes(new MidiFile([new TrackChunk(new SequenceTrackNameEvent("Conductor")), .. tracks]) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) });

    private static byte[] Bytes(MidiFile file)
    {
        using var stream = new MemoryStream();
        file.Write(stream, MidiFileFormat.MultiTrack);
        return stream.ToArray();
    }
}
