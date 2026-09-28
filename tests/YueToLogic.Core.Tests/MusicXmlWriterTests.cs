using System.Xml.Linq;
using YueToLogic.Core.Arrangement;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Model;
using YueToLogic.Core.MusicXml;
using static YueToLogic.Core.Tests.TestScores;

namespace YueToLogic.Core.Tests;

public class MusicXmlWriterTests
{
    [Fact]
    public void Sample_becomes_a_partwise_score_with_chord_symbols_sections_and_tempo()
    {
        var score = Convert(File.ReadAllText(SamplePath));

        var document = Read(new MusicXmlWriter().Write(score, title: "Sample"));

        var root = document.Root!;
        Assert.Equal("score-partwise", root.Name.LocalName);
        Assert.Equal("4.0", (string?)root.Attribute("version"));
        Assert.Equal("score-partwise", document.DocumentType?.Name);
        Assert.Equal("Sample", root.Element("work")?.Element("work-title")?.Value);
        // Ins has no notes in the sample and the chords are symbols only, so Vocal is the one part.
        var part = Assert.Single(root.Elements("part"));
        Assert.Equal("Vocal Melody", Assert.Single(root.Descendants("score-part")).Element("part-name")?.Value);
        Assert.Equal("P1", (string?)part.Attribute("id"));

        Assert.Equal(56, part.Descendants("note").Count(n => n.Element("pitch") is not null && n.Element("tie")?.Attribute("type")?.Value != "stop"));
        Assert.Equal(8, part.Descendants("harmony").Count());
        Assert.Equal(["verse", "chorus"], part.Descendants("rehearsal").Select(r => r.Value));
        Assert.Equal("88", part.Descendants("sound").Single().Attribute("tempo")?.Value);
        var attributes = part.Elements("measure").First().Element("attributes")!;
        Assert.Equal(Ppq.ToString(), attributes.Element("divisions")?.Value);
        Assert.Equal("4", attributes.Element("time")?.Element("beats")?.Value);
        Assert.Equal("G", attributes.Element("clef")?.Element("sign")?.Value);
        AssertMeasuresAreFull(document, Bar);
    }

    [Fact]
    public void Every_part_but_the_drums_is_written_and_every_measure_adds_up()
    {
        var score = Convert(File.ReadAllText(SamplePath), new ArrangementOptions
        {
            Bass = new BassOptions(),
            Drums = new DrumOptions(),
            Chords = new ChordOptions { Pattern = ChordPattern.ArpeggioUp },
            GuideTones = new GuideToneOptions(),
            CountIn = new CountInOptions { Bars = 2 },
            Groove = new GrooveOptions { Swing = 0.3, HumanizeTimingMs = 20 },
        });

        var document = Read(new MusicXmlWriter().Write(score));

        var names = document.Descendants("score-part").Select(p => p.Element("part-abbreviation")!.Value).ToList();
        Assert.DoesNotContain("Drums", names);
        Assert.Contains("Bass", names);
        Assert.Contains("Chords", names);
        Assert.Equal("F", document.Root!.Elements("part").ElementAt(names.IndexOf("Bass")).Descendants("clef").Single().Element("sign")?.Value);
        // The count-in bars are there, and silent.
        var first = document.Root.Elements("part").First().Elements("measure").First();
        Assert.Equal("yes", first.Element("note")?.Element("rest")?.Attribute("measure")?.Value);
        AssertMeasuresAreFull(document, Bar);
    }

    [Fact]
    public void A_chord_track_can_be_left_out_while_its_symbols_stay()
    {
        var score = Convert(File.ReadAllText(SamplePath), new ArrangementOptions { Chords = new ChordOptions() });

        var document = Read(new MusicXmlWriter().Write(score, new MusicXmlOptions { IncludeChordTrack = false }));

        Assert.DoesNotContain("Chords", document.Descendants("part-abbreviation").Select(p => p.Value));
        Assert.Equal(8, document.Descendants("harmony").Count());
    }

    [Fact]
    public void A_note_over_the_bar_line_is_tied_and_overlapping_notes_share_slices()
    {
        // A whole note from beat 3 of bar 1, and a half note starting under it on beat 4.
        var score = Score(
            new NoteEvent(2 * Ppq, 4 * Ppq, 64),
            new NoteEvent(3 * Ppq, 2 * Ppq, 67));

        var document = Read(new MusicXmlWriter().Write(score));

        var measures = document.Descendants("measure").ToList();
        Assert.Equal(2, measures.Count);
        var notes = measures[0].Elements("note").ToList();
        // Bar 1: a half rest, E alone on beat 3, then E and G together on beat 4.
        Assert.Equal("half", notes[0].Element("type")?.Value);
        Assert.NotNull(notes[0].Element("rest"));
        Assert.Equal(["E4"], Pitches(notes[1]));
        Assert.Equal("start", notes[1].Element("tie")?.Attribute("type")?.Value);
        Assert.Equal(["G4"], Pitches(notes[3]));
        Assert.NotNull(notes[3].Element("chord"));
        // Bar 2: E and G tied in on beat 1, where G ends and E goes on, then E alone on beat 2.
        var second = measures[1].Elements("note").ToList();
        Assert.Equal(["stop", "start"], second[0].Elements("tie").Select(t => t.Attribute("type")!.Value));
        Assert.Equal(["G4"], Pitches(second[1]));
        Assert.Equal(["stop"], second[1].Elements("tie").Select(t => t.Attribute("type")!.Value));
        Assert.Equal(["E4"], Pitches(second[2]));
        Assert.Equal("stop", second[2].Element("tie")?.Attribute("type")?.Value);
        Assert.Null(second[2].Elements("tie").FirstOrDefault(t => t.Attribute("type")?.Value == "start"));
        AssertMeasuresAreFull(document, Bar);
    }

    [Fact]
    public void Odd_lengths_are_split_into_note_values_and_keep_their_exact_duration()
    {
        // Seven sixteenths (a dotted quarter and a sixteenth) and a triplet eighth, then silence.
        var score = Score(
            new NoteEvent(0, 7 * Ppq / 4, 60),
            new NoteEvent(7 * Ppq / 4, Ppq / 3, 62));

        var document = Read(new MusicXmlWriter().Write(score));

        var notes = document.Descendants("note").Where(n => n.Element("pitch") is not null).ToList();
        Assert.Equal(["quarter", "16th"], notes.Take(2).Select(n => n.Element("type")!.Value));
        Assert.NotNull(notes[0].Element("dot"));
        Assert.Equal("start", notes[0].Element("tie")?.Attribute("type")?.Value);
        Assert.Equal(Ppq / 3, notes.Skip(2).Sum(n => int.Parse(n.Element("duration")!.Value)));
        AssertMeasuresAreFull(document, Bar);
    }

    [Fact]
    public void Flat_keys_spell_flats_and_seventh_suspended_chords_carry_their_degrees()
    {
        var score = Convert(Native("""
            %verse
            [V:Vocal] "Bb7sus4" _B,4 _E4 z8 |
            [V:Ins] z16 |
            """, key: "Bb"));

        var document = Read(new MusicXmlWriter().Write(score));

        Assert.Equal("-2", document.Descendants("fifths").First().Value);
        var harmony = document.Descendants("harmony").Single();
        Assert.Equal("B", harmony.Element("root")?.Element("root-step")?.Value);
        Assert.Equal("-1", harmony.Element("root")?.Element("root-alter")?.Value);
        Assert.Equal("dominant", harmony.Element("kind")?.Value);
        Assert.Equal("7sus4", harmony.Element("kind")?.Attribute("text")?.Value);
        Assert.Equal(["3 subtract", "4 add"], harmony.Elements("degree").Select(d => $"{d.Element("degree-value")!.Value} {d.Element("degree-type")!.Value}"));
        var pitches = document.Descendants("note").SelectMany(Pitches).ToList();
        Assert.Equal(["Bb3", "Eb4"], pitches);
    }

    private static ScoreDocument Convert(string abc, ArrangementOptions? arrangement = null)
    {
        var result = new ScoreConverter().Convert(abc, new ConversionOptions { Arrangement = arrangement ?? new ArrangementOptions() });
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Score!;
    }

    private static ScoreDocument Score(params NoteEvent[] notes) => new(
        Ppq,
        null,
        120,
        [new TimeSignatureChange(0, 4, 4)],
        [new KeySignatureChange(0, "C", 0, false)],
        [],
        [new VoiceTrack("Vocal", "Vocal", notes)],
        [],
        notes.Max(n => n.StartTicks + n.DurationTicks));

    private static XDocument Read(byte[] xml)
    {
        using var stream = new MemoryStream(xml);
        return XDocument.Load(stream);
    }

    /// <summary>A pitched note as <c>E4</c>, <c>Bb3</c>, or nothing for a rest.</summary>
    private static IEnumerable<string> Pitches(XElement note)
    {
        var pitch = note.Element("pitch");
        if (pitch is null)
        {
            yield break;
        }
        var alter = pitch.Element("alter")?.Value switch { "1" => "#", "-1" => "b", _ => "" };
        yield return $"{pitch.Element("step")!.Value}{alter}{pitch.Element("octave")!.Value}";
    }

    /// <summary>Every measure of every part lasts exactly one bar: what a notation program needs to line the parts up.</summary>
    private static void AssertMeasuresAreFull(XDocument document, long barTicks)
    {
        foreach (var part in document.Root!.Elements("part"))
        {
            foreach (var measure in part.Elements("measure"))
            {
                var length = measure.Elements("note")
                    .Where(n => n.Element("chord") is null)
                    .Sum(n => long.Parse(n.Element("duration")!.Value));
                Assert.True(length == barTicks, $"Part {part.Attribute("id")?.Value}, measure {measure.Attribute("number")?.Value} lasts {length} ticks.");
            }
        }
    }
}
