using System.Globalization;
using System.Text;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Abc;

public interface IAbcScoreWriter
{
    /// <summary>Writes the voices <c>Vocal</c> and <c>Ins</c> and the chords of a score as a YuE2 <c>score.abc</c>.</summary>
    string Write(ScoreDocument score);
}

/// <summary>
/// Writes a <see cref="ScoreDocument"/> in the ABC dialect YuE2 reads and writes, the reverse of
/// <see cref="AbcScoreParser"/>. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// The output follows what YuE2 itself writes, as seen in its <c>score.abc</c> files: <c>L:1/16</c>, the two voices
/// <c>Vocal</c> and <c>Ins</c> declared in the header, groups of up to four bars with every voice on a line of its
/// own, a section comment in front of the group a section starts with, the chord in effect written at the start of
/// every bar of <c>Vocal</c> (and wherever it changes), <c>z16</c> for a bar of <c>Vocal</c> without notes and
/// <c>Z</c>/<c>Z4</c> for bars of <c>Ins</c> without any. Lengths are the multiples YuE2 uses; anything else is
/// tied from them. Accidentals follow the YuE2 rule the parser applies: one holds for its letter in every octave
/// until the bar line.
/// <para>
/// Only <c>Vocal</c> and <c>Ins</c> are written, and each of them as a single line of melody: a note is cut off
/// where the next one starts. Positions are rounded to sixteenths. Meter and key changes start a new group and
/// are written after each <c>V:</c> line, which is where the parser takes them for every voice.
/// </para>
/// </remarks>
public sealed class AbcScoreWriter : IAbcScoreWriter
{
    public const string VocalVoiceId = "Vocal";
    public const string InsVoiceId = "Ins";

    private const int UnitsPerWhole = 16;
    private const int BarsPerGroup = 4;

    /// <summary>The note lengths YuE2 writes, in sixteenths, longest first (see <see cref="AbcLength.IsNative"/>).</summary>
    private static readonly int[] NativeLengths = [48, 32, 24, 16, 12, 8, 6, 4, 3, 2, 1];

    private static readonly (string Id, string Declaration)[] Voices =
    [
        (VocalVoiceId, "clef=treble name=\"Vocal Melody\" snm=\"Vocal\""),
        (InsVoiceId, "clef=treble name=\"Ins Melody\" snm=\"Inst.\""),
    ];

    public string Write(ScoreDocument score)
    {
        ArgumentNullException.ThrowIfNull(score);
        return new WriteSession(score).Run();
    }

    private sealed class WriteSession
    {
        private readonly ScoreDocument _score;
        private readonly double _unitTicks;
        private readonly BarGrid _grid;
        private readonly AbcKey[] _barKeys;
        private readonly List<string>[] _barSections;
        private readonly List<UnitChord> _chords;
        private readonly Dictionary<string, List<GridNote>> _notes = [];
        private readonly StringBuilder _out = new();

        public WriteSession(ScoreDocument score)
        {
            _score = score;
            _unitTicks = score.TicksPerQuarterNote * 4.0 / UnitsPerWhole;

            foreach (var (id, _) in Voices)
            {
                var voice = score.Voices.FirstOrDefault(v => v.Id == id && v.Kind == TrackKind.Melody);
                _notes[id] = Monophonic(voice?.Notes ?? []);
            }

            _chords = score.Chords
                .Select(c => new UnitChord(Units(c.StartTicks), Units(c.StartTicks + c.DurationTicks), c.Text))
                .Where(c => c.End > c.Start)
                .OrderBy(c => c.Start)
                .ToList();

            var length = new[] { Units(score.LengthTicks), 1L }
                .Concat(_notes.Values.SelectMany(n => n).Select(n => n.End))
                .Max();
            _grid = new BarGrid(Meters(score), length);

            _barKeys = KeysPerBar(score);
            _barSections = new List<string>[_grid.Bars.Count];
            foreach (var section in score.Sections)
            {
                var start = Units(section.StartTicks);
                var name = OneLine(section.Name);
                if (start < _grid.End && name.Length > 0)
                {
                    (_barSections[_grid.IndexAt(start)] ??= []).Add(name);
                }
            }
        }

        public string Run()
        {
            WriteHeader();
            var bars = _grid.Bars;
            var trackers = Voices.ToDictionary(v => v.Id, _ => new VoiceState());
            var first = 0;
            while (first < bars.Count)
            {
                var count = 1;
                while (first + count < bars.Count && count < BarsPerGroup && !StartsGroup(first + count))
                {
                    count++;
                }

                foreach (var name in _barSections[first] ?? [])
                {
                    Line($"% {name}");
                }

                foreach (var (id, _) in Voices)
                {
                    Line($"V: {id}");
                    if (first > 0 && !bars[first].SameMeter(bars[first - 1]))
                    {
                        Line(Invariant($"M:{bars[first].Numerator}/{bars[first].Denominator}"));
                    }

                    if (first > 0 && _barKeys[first] != _barKeys[first - 1])
                    {
                        Line($"K:{_barKeys[first].Name}");
                    }

                    Line(WriteBars(id, first, count, trackers[id]));
                }

                first += count;
            }

            return _out.ToString();
        }

        private void WriteHeader()
        {
            var bar = _grid.Bars[0];
            Line("X:1");
            Line($"T:{OneLine(_score.Title ?? string.Empty)}");
            Line(Invariant($"M:{bar.Numerator}/{bar.Denominator}"));
            Line("L:1/16");
            Line($"Q:1/4={_score.TempoBpm.ToString("0.##", CultureInfo.InvariantCulture)}");
            foreach (var (id, declaration) in Voices)
            {
                Line($"V: {id} {declaration}");
            }

            Line($"K:{_barKeys[0].Name}");
        }

        /// <summary>A section, a meter change and a key change each open a new group, as they do in YuE2's scores.</summary>
        private bool StartsGroup(int bar) =>
            _barSections[bar] is { Count: > 0 }
            || !_grid.Bars[bar].SameMeter(_grid.Bars[bar - 1])
            || _barKeys[bar] != _barKeys[bar - 1];

        /// <summary>One line of a voice: its bars of the group, empty ones gathered into multi-measure rests.</summary>
        private string WriteBars(string voiceId, int first, int count, VoiceState state)
        {
            var line = new StringBuilder();
            var resting = 0;
            for (var index = first; index < first + count; index++)
            {
                var bar = WriteBar(voiceId, _grid.Bars[index], _barKeys[index], state);
                if (bar is null)
                {
                    resting++;
                    continue;
                }

                AppendRest(line, resting);
                resting = 0;
                line.Append(bar).Append('|');
            }

            AppendRest(line, resting);
            return line.ToString();

            static void AppendRest(StringBuilder line, int bars)
            {
                if (bars > 0)
                {
                    line.Append(bars == 1 ? "Z|" : Invariant($"Z{bars}|"));
                }
            }
        }

        /// <returns>The bar without its bar line, or <c>null</c> if it has neither notes nor a chord.</returns>
        private string? WriteBar(string voiceId, GridBar bar, AbcKey key, VoiceState state)
        {
            state.Accidentals.Reset();
            var notes = _notes[voiceId];
            var chords = voiceId == VocalVoiceId;
            var opening = chords ? ChordAt(bar.Start) : null;
            var firstNote = notes.FindIndex(n => n.End > bar.Start);
            var hasNotes = firstNote >= 0 && notes[firstNote].Start < bar.End;
            if (!hasNotes && opening is null && !(chords && _chords.Exists(c => c.Start > bar.Start && c.Start < bar.End)))
            {
                return null;
            }

            var text = new StringBuilder();
            var position = bar.Start;
            var noteIndex = Math.Max(firstNote, 0);
            while (position < bar.End)
            {
                if (chords && (position == bar.Start ? opening : _chords.Find(c => c.Start == position)) is { } chord)
                {
                    text.Append('"').Append(chord.Text).Append('"');
                }

                var end = bar.End;
                if (chords && _chords.Find(c => c.Start > position && c.Start < end) is { } change)
                {
                    end = change.Start;
                }

                while (noteIndex < notes.Count && notes[noteIndex].End <= position)
                {
                    noteIndex++;
                }

                if (noteIndex < notes.Count && notes[noteIndex].Start <= position)
                {
                    var note = notes[noteIndex];
                    end = Math.Min(end, note.End);
                    WriteNote(text, note, position, end, key, state);
                }
                else
                {
                    if (noteIndex < notes.Count)
                    {
                        end = Math.Min(end, notes[noteIndex].Start);
                    }

                    foreach (var part in Split(end - position))
                    {
                        text.Append('z').Append(LengthText(part));
                    }
                }

                position = end;
            }

            return text.ToString();
        }

        /// <summary>
        /// The part of a note between two positions, tied into what follows it. Only its first part carries an
        /// accidental; every continuation repeats the letter and octave unmarked, which the parser reads as the
        /// tied pitch whatever the bar says.
        /// </summary>
        private static void WriteNote(StringBuilder text, GridNote note, long from, long to, AbcKey key, VoiceState state)
        {
            if (from == note.Start)
            {
                state.Spelling = Spell(note.Pitch, key, state.Accidentals);
                text.Append(state.Spelling.Accidental);
            }

            var parts = Split(to - from);
            for (var i = 0; i < parts.Count; i++)
            {
                text.Append(state.Spelling.Written).Append(LengthText(parts[i]));
                if (i < parts.Count - 1 || to < note.End)
                {
                    text.Append('-');
                }
            }
        }

        /// <summary>
        /// Letter, octave and accidental of a pitch. A letter the key or an earlier accidental in the bar already
        /// sets to the pitch needs no mark; otherwise the natural letter, then a sharp in sharp keys and a flat in
        /// flat keys, then the other one.
        /// </summary>
        private static Spelling Spell(int pitch, AbcKey key, AccidentalTracker accidentals)
        {
            var pitchClass = ((pitch % 12) + 12) % 12;
            var sharpsFirst = key.Sharps >= 0;
            char letter = default;
            var alteration = 0;
            var found = false;
            foreach (var candidate in "CDEFGAB")
            {
                var current = accidentals.Resolve(candidate, null, key.GetAlteration(candidate));
                if (Is(candidate, current))
                {
                    (letter, alteration, found) = (candidate, current, true);
                    break;
                }
            }

            foreach (var wanted in sharpsFirst ? new[] { 0, 1, -1 } : [0, -1, 1])
            {
                if (found)
                {
                    break;
                }

                foreach (var candidate in "CDEFGAB")
                {
                    if (Is(candidate, wanted))
                    {
                        (letter, alteration, found) = (candidate, wanted, true);
                        break;
                    }
                }
            }

            var mark = string.Empty;
            if (alteration != accidentals.Resolve(letter, null, key.GetAlteration(letter)))
            {
                mark = alteration switch { 1 => "^", -1 => "_", _ => "=" };
                accidentals.Resolve(letter, alteration, key.GetAlteration(letter));
            }

            var octave = (pitch - alteration - 60 - NoteNames.NaturalSemitone(letter)) / 12;
            var written = octave >= 1
                ? char.ToLowerInvariant(letter) + new string('\'', octave - 1)
                : letter + new string(',', -octave);
            return new Spelling(mark, written);

            bool Is(char candidate, int alteration) =>
                (((NoteNames.NaturalSemitone(candidate) + alteration) % 12) + 12) % 12 == pitchClass;
        }

        /// <summary>A length as the native lengths it is tied from, longest first: 10 is 8 and 2, 5 is 4 and 1.</summary>
        private static List<int> Split(long length)
        {
            var parts = new List<int>();
            while (length > 0)
            {
                var part = NativeLengths.First(n => n <= length);
                parts.Add(part);
                length -= part;
            }

            return parts;
        }

        private static string LengthText(int units) => units == 1 ? string.Empty : units.ToString(CultureInfo.InvariantCulture);

        private UnitChord? ChordAt(long position) => _chords.FindLast(c => c.Start <= position && c.End > position);

        /// <summary>The notes on the grid and one at a time: a note ends where the next one begins, the higher one of two starting together is kept.</summary>
        private List<GridNote> Monophonic(IReadOnlyList<NoteEvent> notes)
        {
            var ordered = notes
                .Select(n => new GridNote(Units(n.StartTicks), Math.Max(Units(n.StartTicks) + 1, Units(n.StartTicks + n.DurationTicks)), n.NoteNumber))
                .Where(n => n.Start >= 0)
                .OrderBy(n => n.Start)
                .ThenByDescending(n => n.Pitch)
                .ToList();
            var result = new List<GridNote>();
            foreach (var note in ordered)
            {
                if (result.Count > 0 && result[^1].Start == note.Start)
                {
                    continue;
                }

                if (result.Count > 0 && result[^1].End > note.Start)
                {
                    result[^1] = result[^1] with { End = note.Start };
                }

                result.Add(note);
            }

            return result;
        }

        private List<MeterAt> Meters(ScoreDocument score) =>
            score.TimeSignatures
                .Where(s => s.Numerator > 0 && s.Denominator is > 0 and <= UnitsPerWhole && UnitsPerWhole % s.Denominator == 0)
                .Select(s => new MeterAt(Units(s.StartTicks), s.Numerator, s.Denominator))
                .OrderBy(s => s.Start)
                .ToList();

        /// <summary>The key of every bar; a change inside a bar holds for all of it, since K: fields stand at group starts.</summary>
        private AbcKey[] KeysPerBar(ScoreDocument score)
        {
            var keys = score.KeySignatures
                .Select(k => (Start: Units(k.StartTicks), Key: AbcKey.TryParse(k.Key, out var parsed) ? parsed : AbcKey.FromSignature(k.Sharps, k.IsMinor)))
                .OrderBy(k => k.Start)
                .ToList();
            var result = new AbcKey[_grid.Bars.Count];
            var current = keys.Count > 0 ? keys[0].Key : AbcKey.CMajor;
            var next = 0;
            for (var i = 0; i < result.Length; i++)
            {
                while (next < keys.Count && keys[next].Start < _grid.Bars[i].End)
                {
                    current = keys[next++].Key;
                }

                result[i] = current;
            }

            return result;
        }

        private long Units(long ticks) => (long)Math.Round(ticks / _unitTicks, MidpointRounding.AwayFromZero);

        private void Line(string text) => _out.Append(text).Append('\n');

        private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record UnitChord(long Start, long End, string Text);

    /// <param name="Accidental">The mark written in front of the note, if any.</param>
    /// <param name="Written">Letter and octave marks, repeated unmarked by every tied continuation.</param>
    private sealed record Spelling(string Accidental, string Written);

    /// <summary>What a voice carries from bar to bar: its accidentals and the spelling of the note that may be tied on.</summary>
    private sealed class VoiceState
    {
        public AccidentalTracker Accidentals { get; } = new();

        public Spelling Spelling { get; set; } = new(string.Empty, "C");
    }
}
