using System.Globalization;
using System.Text;
using System.Xml;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.MusicXml;

/// <summary>Writes a score as MusicXML, for notation programs such as MuseScore or a score viewer on the iPad.</summary>
public interface IMusicXmlWriter
{
    /// <param name="score">The score as converted, i.e. with octave shifts, generated tracks and count-in.</param>
    /// <param name="title">Shown as the work title where the score has none.</param>
    byte[] Write(ScoreDocument score, MusicXmlOptions? options = null, string? title = null);
}

public sealed record MusicXmlOptions
{
    /// <summary>Whether a generated chord track (a chord pattern) becomes a part; the chord symbols are written either way.</summary>
    public bool IncludeChordTrack { get; init; } = true;
}

/// <summary>
/// A partwise MusicXML 4.0 document, one part per track, with the chord symbols (<c>harmony</c>), the sections
/// (rehearsal marks) and the tempo above the first part.
/// </summary>
/// <remarks>
/// Every part is one voice. A track is cut at every bar line and at every point where one of its notes starts or
/// ends; each slice holds the pitches sounding throughout it as one chord, and a note spanning slices is tied
/// across them. That writes any overlap a generated track may have (a guide-tone pad under a bass, a chord pattern)
/// correctly, at the cost of more ties than a copyist would use; notation programs re-beam and re-spell anyway.
/// Durations are split greedily into the note values from a whole to a 128th, dotted ones included; a remainder
/// below that (a triplet, a humanized note) goes into the last piece's duration, which stays exact while its drawn
/// value is the nearest one. Drums are left out: they are generated from a pattern, not written, and General MIDI
/// drum notes as pitches would be nonsense on a staff. Dynamics (velocities) are left out as well.
/// </remarks>
public sealed class MusicXmlWriter : IMusicXmlWriter
{
    private static readonly string[] SharpSteps = ["C", "C", "D", "D", "E", "F", "F", "G", "G", "A", "A", "B"];
    private static readonly int[] SharpAlters = [0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0];
    private static readonly string[] FlatSteps = ["C", "D", "D", "E", "E", "F", "G", "G", "A", "A", "B", "B"];
    private static readonly int[] FlatAlters = [0, -1, 0, -1, 0, 0, -1, 0, -1, 0, -1, 0];

    public byte[] Write(ScoreDocument score, MusicXmlOptions? options = null, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        options ??= new MusicXmlOptions();

        var parts = score.Voices
            .Where(voice => voice.Kind != TrackKind.Drums && voice.Notes.Count > 0)
            .Where(voice => options.IncludeChordTrack || voice.Kind != TrackKind.Chords)
            .ToList();
        // Chord symbols and sections need a staff to stand above, even in a score without any notes.
        if (parts.Count == 0)
        {
            parts.Add(new VoiceTrack("Score", "Score", []));
        }

        var bars = BarsOf(score, parts);
        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartDocument();
            xml.WriteDocType("score-partwise", "-//Recordare//DTD MusicXML 4.0 Partwise//EN", "http://www.musicxml.org/dtds/partwise.dtd", null);
            xml.WriteStartElement("score-partwise");
            xml.WriteAttributeString("version", "4.0");

            var name = string.IsNullOrWhiteSpace(score.Title) ? title : score.Title;
            if (!string.IsNullOrWhiteSpace(name))
            {
                xml.WriteStartElement("work");
                xml.WriteElementString("work-title", name.Trim());
                xml.WriteEndElement();
            }
            xml.WriteStartElement("identification");
            xml.WriteStartElement("encoding");
            xml.WriteElementString("software", "YuE UI");
            xml.WriteEndElement();
            xml.WriteEndElement();

            xml.WriteStartElement("part-list");
            for (var index = 0; index < parts.Count; index++)
            {
                xml.WriteStartElement("score-part");
                xml.WriteAttributeString("id", PartId(index));
                xml.WriteElementString("part-name", parts[index].DisplayName);
                xml.WriteElementString("part-abbreviation", parts[index].Id);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();

            for (var index = 0; index < parts.Count; index++)
            {
                new PartWriter(xml, score, bars, parts[index], index == 0).Write(PartId(index));
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return buffer.ToArray();
    }

    private static string PartId(int index) => $"P{index + 1}";

    /// <summary>
    /// The bars from the meter changes, the same way the MIDI file and the piano roll count them; a change off a bar
    /// line takes effect at the next one. They run to the end of the song or of its last note, whichever is later.
    /// </summary>
    private static List<Bar> BarsOf(ScoreDocument score, IReadOnlyList<VoiceTrack> parts)
    {
        var end = Math.Max(
            score.LengthTicks,
            parts.SelectMany(part => part.Notes).Select(note => note.StartTicks + note.DurationTicks).DefaultIfEmpty(0).Max());
        var signatures = score.TimeSignatures.Count > 0 ? score.TimeSignatures : [new TimeSignatureChange(0, 4, 4)];
        var bars = new List<Bar>();
        var position = 0L;
        var next = 0;
        var current = signatures[0];
        while (position < end || bars.Count == 0)
        {
            while (next < signatures.Count && signatures[next].StartTicks <= position)
            {
                current = signatures[next++];
            }
            var length = 4L * score.TicksPerQuarterNote * current.Numerator / current.Denominator;
            bars.Add(new Bar(position, length, current.Numerator, current.Denominator));
            position += length;
        }
        return bars;
    }

    private readonly record struct Bar(long Start, long Length, int Numerator, int Denominator)
    {
        public long End => Start + Length;

        public bool SameMeter(Bar other) => Numerator == other.Numerator && Denominator == other.Denominator;
    }

    /// <summary>A piece of a slice that one note value can draw.</summary>
    private readonly record struct Piece(long Duration, string Type, bool Dotted);

    private sealed class PartWriter(XmlWriter xml, ScoreDocument score, IReadOnlyList<Bar> bars, VoiceTrack part, bool carriesSymbols)
    {
        private readonly List<(long Value, string Type, bool Dotted)> _values = NoteValues(score.TicksPerQuarterNote);

        public void Write(string id)
        {
            var notes = part.Notes
                .Where(note => note.DurationTicks > 0)
                .OrderBy(note => note.StartTicks)
                .ToList();
            var clef = Clef(notes);
            // Chord and section starts cut the first part as well, so their symbols sit right above the note they belong to.
            var chordsAt = carriesSymbols
                ? score.Chords.Where(chord => chord.Symbol is not null).GroupBy(chord => chord.StartTicks).ToDictionary(group => group.Key, group => group.First())
                : [];
            var sectionsAt = carriesSymbols
                ? score.Sections.GroupBy(section => section.StartTicks).ToDictionary(group => group.Key, group => group.Last().Name)
                : [];

            xml.WriteStartElement("part");
            xml.WriteAttributeString("id", id);
            KeySignatureChange? key = null;
            for (var index = 0; index < bars.Count; index++)
            {
                var bar = bars[index];
                xml.WriteStartElement("measure");
                xml.WriteAttributeString("number", Number(index + 1));

                var barKey = KeyAt(bar.Start);
                var meterChanged = index == 0 || !bar.SameMeter(bars[index - 1]);
                var keyChanged = barKey is not null && (key is null || barKey.Sharps != key.Sharps || barKey.IsMinor != key.IsMinor);
                if (index == 0 || meterChanged || keyChanged)
                {
                    WriteAttributes(bar, barKey, index == 0 ? clef : null, keyChanged || index == 0, meterChanged);
                }
                key = barKey ?? key;
                if (index == 0 && carriesSymbols)
                {
                    WriteTempo();
                }

                var anchors = new SortedSet<long>(
                    chordsAt.Keys.Concat(sectionsAt.Keys).Where(tick => tick > bar.Start && tick < bar.End));
                var slices = Slices(bar, notes, anchors);
                if (slices.Count == 1 && slices[0].Pitches.Count == 0 && !chordsAt.ContainsKey(bar.Start) && !sectionsAt.ContainsKey(bar.Start))
                {
                    WriteMeasureRest(bar);
                }
                else
                {
                    foreach (var slice in slices)
                    {
                        if (sectionsAt.TryGetValue(slice.Start, out var section))
                        {
                            WriteRehearsal(section);
                        }
                        if (chordsAt.TryGetValue(slice.Start, out var chord))
                        {
                            WriteHarmony(chord.Symbol!, chord.Text, KeyAt(slice.Start));
                        }
                        WriteSlice(slice, KeyAt(slice.Start));
                    }
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        private KeySignatureChange? KeyAt(long tick) => score.KeySignatures.LastOrDefault(change => change.StartTicks <= tick)
            ?? score.KeySignatures.FirstOrDefault();

        /// <summary>A bass clef for a track that lives mostly below the G under middle C, a treble clef otherwise.</summary>
        private static (string Sign, int Line) Clef(IReadOnlyList<NoteEvent> notes)
        {
            if (notes.Count == 0)
            {
                return ("G", 2);
            }
            var pitches = notes.Select(note => note.NoteNumber).Order().ToList();
            return pitches[pitches.Count / 2] < 55 ? ("F", 4) : ("G", 2);
        }

        private void WriteAttributes(Bar bar, KeySignatureChange? key, (string Sign, int Line)? clef, bool withKey, bool withMeter)
        {
            xml.WriteStartElement("attributes");
            if (clef is not null)
            {
                xml.WriteElementString("divisions", Number(score.TicksPerQuarterNote));
            }
            if (withKey)
            {
                xml.WriteStartElement("key");
                xml.WriteElementString("fifths", Number(key?.Sharps ?? 0));
                xml.WriteElementString("mode", key?.IsMinor == true ? "minor" : "major");
                xml.WriteEndElement();
            }
            if (withMeter)
            {
                xml.WriteStartElement("time");
                xml.WriteElementString("beats", Number(bar.Numerator));
                xml.WriteElementString("beat-type", Number(bar.Denominator));
                xml.WriteEndElement();
            }
            if (clef is { } shown)
            {
                xml.WriteStartElement("clef");
                xml.WriteElementString("sign", shown.Sign);
                xml.WriteElementString("line", Number(shown.Line));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        private void WriteTempo()
        {
            var bpm = Math.Round(score.TempoBpm, 2).ToString(CultureInfo.InvariantCulture);
            xml.WriteStartElement("direction");
            xml.WriteAttributeString("placement", "above");
            xml.WriteStartElement("direction-type");
            xml.WriteStartElement("metronome");
            xml.WriteElementString("beat-unit", "quarter");
            xml.WriteElementString("per-minute", bpm);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("sound");
            xml.WriteAttributeString("tempo", bpm);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        private void WriteRehearsal(string name)
        {
            xml.WriteStartElement("direction");
            xml.WriteAttributeString("placement", "above");
            xml.WriteStartElement("direction-type");
            xml.WriteElementString("rehearsal", name);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        private void WriteHarmony(ChordSymbol symbol, string text, KeySignatureChange? key)
        {
            var (kind, degrees) = KindOf(symbol.Quality);
            xml.WriteStartElement("harmony");
            WriteStep("root", symbol.RootPitchClass, key);
            xml.WriteStartElement("kind");
            // What the score wrote, so the symbol reads as it did in YuE rather than in each program's own spelling.
            xml.WriteAttributeString("text", ChordSuffix(text));
            xml.WriteString(kind);
            xml.WriteEndElement();
            if (symbol.BassPitchClass is { } bass)
            {
                WriteStep("bass", bass, key);
            }
            foreach (var (value, type) in degrees)
            {
                xml.WriteStartElement("degree");
                xml.WriteElementString("degree-value", Number(value));
                xml.WriteElementString("degree-alter", "0");
                xml.WriteElementString("degree-type", type);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        /// <summary><c>root</c>/<c>root-step</c>/<c>root-alter</c>, or the same with <c>bass</c>.</summary>
        private void WriteStep(string element, int pitchClass, KeySignatureChange? key)
        {
            var (step, alter) = Spell(pitchClass, key);
            xml.WriteStartElement(element);
            xml.WriteElementString($"{element}-step", step);
            if (alter != 0)
            {
                xml.WriteElementString($"{element}-alter", Number(alter));
            }
            xml.WriteEndElement();
        }

        /// <summary>The chord symbol without its root and bass, e.g. <c>m7</c> from <c>"Am7/G"</c>: MusicXML's <c>text</c> is the kind alone.</summary>
        private static string ChordSuffix(string text)
        {
            var symbol = text.Trim().Trim('"');
            var slash = symbol.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                symbol = symbol[..slash];
            }
            var start = symbol.Length > 0 ? 1 : 0;
            while (start < symbol.Length && symbol[start] is '#' or 'b')
            {
                start++;
            }
            return symbol[start..];
        }

        /// <summary>MusicXML's chord kinds; a 7sus4 has none of its own and is a dominant seventh with the fourth for the third.</summary>
        private static (string Kind, (int Value, string Type)[] Degrees) KindOf(ChordQuality quality) => quality switch
        {
            ChordQuality.Major => ("major", []),
            ChordQuality.Minor => ("minor", []),
            ChordQuality.Diminished => ("diminished", []),
            ChordQuality.Augmented => ("augmented", []),
            ChordQuality.Dominant7 => ("dominant", []),
            ChordQuality.Major7 => ("major-seventh", []),
            ChordQuality.Minor7 => ("minor-seventh", []),
            ChordQuality.Diminished7 => ("diminished-seventh", []),
            ChordQuality.HalfDiminished7 => ("half-diminished", []),
            ChordQuality.Suspended4 => ("suspended-fourth", []),
            ChordQuality.Suspended2 => ("suspended-second", []),
            ChordQuality.Major6 => ("major-sixth", []),
            ChordQuality.Minor6 => ("minor-sixth", []),
            ChordQuality.Dominant7Suspended4 => ("dominant", [(3, "subtract"), (4, "add")]),
            ChordQuality.MinorMajor7 => ("major-minor", []),
            _ => ("other", []),
        };

        /// <summary>Sharps in a sharp key and in C, flats in a flat key, as the score's own key signature would have them.</summary>
        private static (string Step, int Alter) Spell(int pitchClass, KeySignatureChange? key)
        {
            var flats = key is { Sharps: < 0 };
            return flats ? (FlatSteps[pitchClass], FlatAlters[pitchClass]) : (SharpSteps[pitchClass], SharpAlters[pitchClass]);
        }

        private void WriteMeasureRest(Bar bar)
        {
            xml.WriteStartElement("note");
            xml.WriteStartElement("rest");
            xml.WriteAttributeString("measure", "yes");
            xml.WriteEndElement();
            xml.WriteElementString("duration", Number(bar.Length));
            xml.WriteElementString("voice", "1");
            xml.WriteEndElement();
        }

        private void WriteSlice(Slice slice, KeySignatureChange? key)
        {
            var pieces = Pieces(slice.End - slice.Start);
            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];
                var first = index == 0;
                var last = index == pieces.Count - 1;
                if (slice.Pitches.Count == 0)
                {
                    WriteNote(null, piece, chord: false, tieStop: false, tieStart: false, key);
                }
                else
                {
                    for (var pitch = 0; pitch < slice.Pitches.Count; pitch++)
                    {
                        var sounding = slice.Pitches[pitch];
                        WriteNote(
                            sounding.NoteNumber,
                            piece,
                            chord: pitch > 0,
                            tieStop: !first || sounding.Start < slice.Start,
                            tieStart: !last || sounding.End > slice.End,
                            key);
                    }
                }
            }
        }

        private void WriteNote(int? noteNumber, Piece piece, bool chord, bool tieStop, bool tieStart, KeySignatureChange? key)
        {
            xml.WriteStartElement("note");
            if (chord)
            {
                xml.WriteElementString("chord", null);
            }
            if (noteNumber is { } number)
            {
                var (step, alter) = Spell(number % 12, key);
                // MIDI 60 is C4 in MusicXML's octave numbering. An alter never crosses an octave here: the flat steps
                // are the letters above (D♭ for C♯), whose octave is the pitch's own.
                xml.WriteStartElement("pitch");
                xml.WriteElementString("step", step);
                if (alter != 0)
                {
                    xml.WriteElementString("alter", Number(alter));
                }
                xml.WriteElementString("octave", Number(number / 12 - 1));
                xml.WriteEndElement();
            }
            else
            {
                xml.WriteElementString("rest", null);
            }
            xml.WriteElementString("duration", Number(piece.Duration));
            if (tieStop)
            {
                WriteTie("tie", "stop");
            }
            if (tieStart)
            {
                WriteTie("tie", "start");
            }
            xml.WriteElementString("voice", "1");
            xml.WriteElementString("type", piece.Type);
            if (piece.Dotted)
            {
                xml.WriteElementString("dot", null);
            }
            if (tieStop || tieStart)
            {
                xml.WriteStartElement("notations");
                if (tieStop)
                {
                    WriteTie("tied", "stop");
                }
                if (tieStart)
                {
                    WriteTie("tied", "start");
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        private void WriteTie(string element, string type)
        {
            xml.WriteStartElement(element);
            xml.WriteAttributeString("type", type);
            xml.WriteEndElement();
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>A stretch of a bar in which the same pitches sound throughout, or none.</summary>
        private sealed record Slice(long Start, long End, List<Sounding> Pitches);

        /// <param name="Start">Where the note sounding this pitch starts, which says whether it is tied in.</param>
        private readonly record struct Sounding(int NoteNumber, long Start, long End);

        /// <summary>
        /// The bar cut at every note start and end and at every anchor (a chord or section inside it). Rests next to
        /// each other merge, unless an anchor sits between them.
        /// </summary>
        private static List<Slice> Slices(Bar bar, IReadOnlyList<NoteEvent> notes, SortedSet<long> anchors)
        {
            var inBar = notes
                .Where(note => note.StartTicks < bar.End && note.StartTicks + note.DurationTicks > bar.Start)
                .ToList();
            var cuts = new SortedSet<long>(anchors) { bar.Start, bar.End };
            foreach (var note in inBar)
            {
                cuts.Add(Math.Max(bar.Start, note.StartTicks));
                cuts.Add(Math.Min(bar.End, note.StartTicks + note.DurationTicks));
            }

            var slices = new List<Slice>();
            var points = cuts.ToList();
            for (var index = 0; index + 1 < points.Count; index++)
            {
                var (start, end) = (points[index], points[index + 1]);
                var sounding = inBar
                    .Where(note => note.StartTicks <= start && note.StartTicks + note.DurationTicks >= end)
                    .GroupBy(note => note.NoteNumber)
                    .Select(group => group.OrderBy(note => note.StartTicks).First())
                    .Select(note => new Sounding(note.NoteNumber, note.StartTicks, note.StartTicks + note.DurationTicks))
                    .OrderBy(entry => entry.NoteNumber)
                    .ToList();
                if (sounding.Count == 0 && slices.Count > 0 && slices[^1].Pitches.Count == 0 && !anchors.Contains(start))
                {
                    slices[^1] = slices[^1] with { End = end };
                }
                else
                {
                    slices.Add(new Slice(start, end, sounding));
                }
            }
            return slices;
        }

        /// <summary>The longest note value that fits, again and again; a remainder too short for any is added to the last piece.</summary>
        private List<Piece> Pieces(long length)
        {
            var pieces = new List<Piece>();
            var left = length;
            while (left > 0)
            {
                var fit = _values.FirstOrDefault(value => value.Value <= left);
                if (fit.Value == 0)
                {
                    if (pieces.Count == 0)
                    {
                        var smallest = _values[^1];
                        pieces.Add(new Piece(left, smallest.Type, smallest.Dotted));
                    }
                    else
                    {
                        pieces[^1] = pieces[^1] with { Duration = pieces[^1].Duration + left };
                    }
                    break;
                }
                pieces.Add(new Piece(fit.Value, fit.Type, fit.Dotted));
                left -= fit.Value;
            }
            return pieces;
        }

        /// <summary>Note values in ticks, longest first, the dotted ones only where they come out in whole ticks.</summary>
        private static List<(long Value, string Type, bool Dotted)> NoteValues(int ticksPerQuarter)
        {
            (string Type, double Quarters)[] plain =
            [
                ("whole", 4), ("half", 2), ("quarter", 1), ("eighth", 0.5), ("16th", 0.25), ("32nd", 0.125), ("64th", 0.0625), ("128th", 0.03125),
            ];
            var values = new List<(long, string, bool)>();
            foreach (var (type, quarters) in plain)
            {
                var ticks = quarters * ticksPerQuarter;
                if (ticks >= 1 && ticks == Math.Floor(ticks))
                {
                    values.Add(((long)ticks, type, false));
                    var dotted = ticks * 1.5;
                    if (dotted == Math.Floor(dotted))
                    {
                        values.Add(((long)dotted, type, true));
                    }
                }
            }
            return values.OrderByDescending(value => value.Item1).ToList();
        }
    }
}
