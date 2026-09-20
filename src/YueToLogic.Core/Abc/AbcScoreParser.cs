using System.Globalization;
using System.Text.RegularExpressions;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Abc;

public interface IAbcScoreParser
{
    AbcParseResult Parse(string abcText, AbcParseOptions? options = null);
}

public sealed record AbcParseOptions
{
    public const int DefaultTicksPerQuarterNote = 480;

    /// <summary>Resolution of the resulting <see cref="ScoreDocument"/>, 1–32767 (the MIDI limit).</summary>
    public int TicksPerQuarterNote { get; init; } = DefaultTicksPerQuarterNote;
}

/// <param name="Score">The parsed score, or <c>null</c> if an error prevented reading it.</param>
public sealed record AbcParseResult(ScoreDocument? Score, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Score is not null;
}

/// <summary>
/// Reads the two-voice ABC dialect that YuE2 writes to <c>score.abc</c>. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// The parser is lenient: anything outside the dialect is reported as a diagnostic and skipped, so a
/// slightly damaged or hand-edited score still produces a usable result. Only a score that cannot be
/// placed on a tick grid at all fails.
/// </remarks>
public sealed partial class AbcScoreParser : IAbcScoreParser
{
    public AbcParseResult Parse(string abcText, AbcParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(abcText);
        return new ParseSession((options ?? new AbcParseOptions()).TicksPerQuarterNote).Run(abcText);
    }

    private sealed partial class ParseSession(int ticksPerQuarterNote)
    {
        private const string ChordVoiceId = "Vocal";

        private readonly int _ppq = ticksPerQuarterNote;
        private readonly DiagnosticBag _diagnostics = new();
        private readonly Dictionary<string, VoiceCursor> _voices = new(StringComparer.Ordinal);
        private readonly List<VoiceCursor> _voiceOrder = [];
        private readonly List<(string Id, string DisplayName)> _declaredVoices = [];
        private readonly Timeline<AbcMeter> _meters = new();
        private readonly Timeline<AbcKey> _keys = new();
        private readonly List<SectionMarker> _sections = [];
        private readonly List<PendingChord> _chords = [];

        private string[] _lines = [];
        private bool _inHeader = true;
        private bool _fatal;
        private int _line;
        private string? _title;
        private AbcMeter? _headerMeter;
        private AbcLength? _unit;
        private double? _tempo;
        private AbcKey? _headerKey;
        private long _groupStart;
        private VoiceCursor? _currentVoice;

        public AbcParseResult Run(string text)
        {
            if (_ppq is < 1 or > short.MaxValue)
            {
                _diagnostics.Error(
                    DiagnosticCodes.TickResolution,
                    Invariant($"Ticks per quarter note must be between 1 and {short.MaxValue}, got {_ppq}."));
                return new AbcParseResult(null, _diagnostics.ToList());
            }

            _lines = text.TrimStart('﻿').Split('\n');
            for (var index = 0; index < _lines.Length && !_fatal; index++)
            {
                _line = index + 1;
                ProcessLine(_lines[index].TrimEnd('\r'));
            }

            _line = 0;
            if (_inHeader && !_fatal)
            {
                EndHeader(keyMissing: true);
            }

            if (_fatal)
            {
                return new AbcParseResult(null, _diagnostics.ToList());
            }

            SynchronizeVoices();
            foreach (var voice in _voiceOrder.Where(v => v.PendingTie is not null))
            {
                _diagnostics.Warning(
                    DiagnosticCodes.TieMismatch,
                    $"Voice '{voice.Id}' ends with an unresolved tie; the note simply ends.");
            }

            if (!_voiceOrder.Exists(v => v.HasMusic))
            {
                _diagnostics.Error(DiagnosticCodes.NoMusic, "The score contains no music lines.");
                return new AbcParseResult(null, _diagnostics.ToList());
            }

            var score = BuildDocument();
            return new AbcParseResult(score, _diagnostics.ToList());
        }

        private void ProcessLine(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            if (trimmed[0] == '%')
            {
                HandleComment(trimmed);
                return;
            }

            var field = FieldPattern().Match(trimmed);
            if (field.Success)
            {
                HandleField(field.Groups[1].Value[0], field.Groups[2].Value.Trim());
                return;
            }

            if (_inHeader)
            {
                EndHeader(keyMissing: true);
                if (_fatal)
                {
                    return;
                }
            }

            HandleMusicLine(line);
        }

        // ---- Header ---------------------------------------------------------------------------

        private void HandleComment(string trimmed)
        {
            if (trimmed.StartsWith("%%", StringComparison.Ordinal))
            {
                _diagnostics.Info(DiagnosticCodes.IgnoredField, $"Directive '{trimmed}' is ignored.", _line);
                return;
            }

            var name = trimmed[1..].Trim();
            if (_inHeader || name.Length == 0)
            {
                return;
            }

            SynchronizeVoices();
            _sections.Add(new SectionMarker(_groupStart, name));
        }

        private void HandleField(char name, string value)
        {
            if (_inHeader)
            {
                switch (name)
                {
                    case 'X':
                        return;
                    case 'T':
                        _title ??= value;
                        return;
                    case 'M':
                        _headerMeter = ParseMeter(value) ?? _headerMeter;
                        return;
                    case 'L':
                        _unit = ParseUnit(value) ?? _unit;
                        return;
                    case 'Q':
                        _tempo = ParseTempo(value) ?? _tempo;
                        return;
                    case 'V':
                        DeclareVoice(value);
                        return;
                    case 'K':
                        _headerKey = ParseKey(value);
                        EndHeader(keyMissing: false);
                        return;
                    default:
                        _diagnostics.Info(DiagnosticCodes.IgnoredField, $"Header field '{name}:' is ignored.", _line);
                        return;
                }
            }

            switch (name)
            {
                case 'V':
                    SwitchVoice(value);
                    break;
                case 'M':
                    if (ParseMeter(value) is { } meter)
                    {
                        ForCurrentVoices(voice => ChangeMeter(voice, meter));
                    }

                    break;
                case 'K':
                    if (ParseKey(value) is { } key)
                    {
                        ForCurrentVoices(voice => ChangeKey(voice, key));
                    }

                    break;
                case 'L':
                    if (ParseUnit(value) is { } unit && IsWholeTickUnit(unit))
                    {
                        _unit = unit;
                    }

                    break;
                case 'Q':
                    if (ParseTempo(value) is { } tempo && Math.Abs(tempo - _tempo!.Value) > 0.001)
                    {
                        _diagnostics.Warning(
                            DiagnosticCodes.TempoChangeIgnored,
                            Invariant($"Tempo changes are not supported; keeping {_tempo} BPM."),
                            _line);
                    }

                    break;
                case 'w' or 'W':
                    _diagnostics.Warning(DiagnosticCodes.UnsupportedNotation, "Lyric lines are not part of the YuE2 dialect and are ignored.", _line);
                    break;
                default:
                    _diagnostics.Info(DiagnosticCodes.IgnoredField, $"Field '{name}:' is ignored.", _line);
                    break;
            }
        }

        private void EndHeader(bool keyMissing)
        {
            _inHeader = false;
            if (keyMissing)
            {
                _diagnostics.Warning(DiagnosticCodes.MissingHeaderField, "The header has no K: field; assuming C major.", _line == 0 ? null : _line);
            }

            if (_headerMeter is null)
            {
                _diagnostics.Warning(DiagnosticCodes.MissingHeaderField, "The header has no valid M: field; assuming 4/4.");
            }

            var meter = _headerMeter ?? AbcMeter.CommonTime;
            if (_unit is null)
            {
                // ABC default: 1/16 for meters below 3/4, otherwise 1/8.
                _unit = meter.Numerator / (double)meter.Denominator < 0.75 ? new AbcLength(1, 16) : new AbcLength(1, 8);
                _diagnostics.Warning(DiagnosticCodes.MissingHeaderField, Invariant($"The header has no valid L: field; using the ABC default {_unit}."));
            }

            if (_tempo is null)
            {
                _tempo = 120;
                _diagnostics.Warning(DiagnosticCodes.MissingHeaderField, "The header has no valid Q: field; assuming 120 BPM.");
            }

            if (!IsWholeTickUnit(_unit.Value) || !meter.TryGetMeasureTicks(_ppq, out _))
            {
                _diagnostics.Error(
                    DiagnosticCodes.TickResolution,
                    Invariant($"L:{_unit} or M:{meter} does not fit a grid of {_ppq} ticks per quarter note; use a resolution divisible by the note length denominator."));
                _fatal = true;
                return;
            }

            _meters.Set(0, meter, fromBody: false);
            _keys.Set(0, _headerKey ?? AbcKey.CMajor, fromBody: false);
            foreach (var (id, displayName) in _declaredVoices)
            {
                CreateVoice(id, displayName);
            }
        }

        private void DeclareVoice(string value)
        {
            var (id, displayName) = ParseVoiceField(value);
            if (id.Length == 0)
            {
                _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, "Voice declaration without an id is ignored.", _line);
                return;
            }

            var existing = _declaredVoices.FindIndex(v => v.Id == id);
            if (existing >= 0)
            {
                _declaredVoices[existing] = (id, displayName ?? _declaredVoices[existing].DisplayName);
            }
            else
            {
                _declaredVoices.Add((id, displayName ?? id));
            }
        }

        // ---- Voices and groups ----------------------------------------------------------------

        private VoiceCursor CreateVoice(string id, string displayName)
        {
            var voice = new VoiceCursor(id, displayName, _voiceOrder.Count, _meters.Latest, _keys.Latest)
            {
                BarStart = _groupStart,
                BarNumber = _voiceOrder.Count > 0 ? _voiceOrder.Max(v => v.BarNumber) : 1,
            };
            _voices[id] = voice;
            _voiceOrder.Add(voice);
            return voice;
        }

        private void SwitchVoice(string value)
        {
            var (id, displayName) = ParseVoiceField(value);
            if (id.Length == 0)
            {
                _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, "V: field without a voice id is ignored.", _line);
                return;
            }

            if (!_voices.TryGetValue(id, out var voice))
            {
                _diagnostics.Info(DiagnosticCodes.UndeclaredVoice, $"Voice '{id}' was not declared in the header.", _line);
                voice = CreateVoice(id, displayName ?? id);
            }

            // YuE2 writes groups of 1-4 measures: every voice in declaration order, then the next group.
            // Returning to an earlier voice therefore starts a new group at a common position.
            if (_currentVoice is null || voice.Order <= _currentVoice.Order)
            {
                SynchronizeVoices();
            }

            _currentVoice = voice;
        }

        /// <summary>
        /// Aligns all voices at the start of a new group. Voices that came up short are padded with rests,
        /// so a faulty group cannot shift everything after it against the audio.
        /// </summary>
        private void SynchronizeVoices()
        {
            var active = _voiceOrder.Where(v => v.HasMusic).ToList();
            foreach (var voice in active.Where(v => v.Offset != 0))
            {
                CloseBar(voice, column: null);
            }

            if (active.Count == 0)
            {
                return;
            }

            var target = active.Max(v => v.BarStart);
            var targetBar = active.Max(v => v.BarNumber);
            if (active.Exists(v => v.BarStart != target))
            {
                var positions = string.Join(", ", active.Select(v => Invariant($"{v.Id} ends before bar {v.BarNumber}")));
                _diagnostics.Warning(
                    DiagnosticCodes.VoicesOutOfSync,
                    $"Voices have different lengths ({positions}); shorter voices are padded with rests.",
                    _line == 0 ? null : _line);
            }

            foreach (var voice in _voiceOrder)
            {
                voice.BarStart = target;
                voice.BarNumber = targetBar;
            }

            _groupStart = target;
        }

        private void ForCurrentVoices(Action<VoiceCursor> action)
        {
            if (_currentVoice is not null)
            {
                action(_currentVoice);
                return;
            }

            _voiceOrder.ForEach(action);
        }

        private void ChangeMeter(VoiceCursor voice, AbcMeter meter)
        {
            if (!meter.TryGetMeasureTicks(_ppq, out _))
            {
                _diagnostics.Warning(DiagnosticCodes.TickResolution, $"Meter {meter} does not fit the tick grid and is ignored.", _line);
                return;
            }

            if (voice.Offset != 0)
            {
                _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, $"Meter change inside bar {voice.BarNumber} of voice '{voice.Id}'.", _line);
            }

            voice.Meter = meter;
            RegisterChange(_meters, voice.Position, meter, "meter");
        }

        private void ChangeKey(VoiceCursor voice, AbcKey key)
        {
            voice.Key = key;
            voice.Accidentals.Reset();
            RegisterChange(_keys, voice.Position, key, "key");
        }

        private void RegisterChange<T>(Timeline<T> timeline, long tick, T value, string what)
            where T : class
        {
            if (!timeline.Set(tick, value, fromBody: true, out var existing))
            {
                _diagnostics.Warning(
                    DiagnosticCodes.ConflictingChange,
                    $"Voices disagree on the {what} at the same position ('{existing}' vs. '{value}'); keeping '{existing}'.",
                    _line);
            }
        }

        // ---- Music lines ----------------------------------------------------------------------

        private void HandleMusicLine(string line)
        {
            var voice = _currentVoice ?? DefaultVoice();
            voice.HasMusic = true;
            foreach (var token in MusicLineParser.Tokenize(StripComment(line)))
            {
                Apply(voice, token);
            }

            if (voice.Offset == 0)
            {
                return;
            }

            voice.Meter.TryGetMeasureTicks(_ppq, out var measureTicks);
            var barTicks = measureTicks * voice.MeasuresInBar;
            if (IsLastMusicLine() && voice.Offset < barTicks)
            {
                // YuE stops writing the score when it reaches its token limit, often inside a bar. A last bar that
                // is complete but has no closing bar line is reported as such below, as in the rest of the score.
                _diagnostics.Warning(
                    DiagnosticCodes.ScoreTruncated,
                    Invariant($"The score ends in the middle of bar {voice.BarNumber} of voice '{voice.Id}' ({Quarters(voice.Offset)} of {Quarters(barTicks)} quarter notes). YuE probably stopped writing it early (its result.json then reports \"truncated\"); the bar is completed with rests."),
                    _line);
                CloseBar(voice, column: null, reportLength: false);
                return;
            }

            _diagnostics.Warning(DiagnosticCodes.MissingBarLine, "Music line does not end with a bar line; the bar is closed here.", _line);
            CloseBar(voice, column: null);
        }

        /// <summary>Whether only blank lines and comments follow the current line.</summary>
        private bool IsLastMusicLine()
        {
            for (var index = _line; index < _lines.Length; index++)
            {
                var rest = _lines[index].Trim();
                if (rest.Length > 0 && rest[0] != '%')
                {
                    return false;
                }
            }

            return true;
        }

        private VoiceCursor DefaultVoice()
        {
            if (_voiceOrder.Count == 0)
            {
                _diagnostics.Info(DiagnosticCodes.UndeclaredVoice, "Music appears before any V: field; using a single voice 'Melody'.", _line);
                CreateVoice("Melody", "Melody");
            }

            _currentVoice = _voiceOrder[0];
            return _currentVoice;
        }

        private void Apply(VoiceCursor voice, AbcToken token)
        {
            switch (token)
            {
                case ChordSymbolToken chord:
                    AddChord(voice, chord);
                    break;
                case InlineKeyToken inlineKey:
                    if (ParseKey(inlineKey.Key, inlineKey.Column) is { } key)
                    {
                        ChangeKey(voice, key);
                    }

                    break;
                case NoteToken note:
                    AddNote(voice, note);
                    break;
                case RestToken rest:
                    AddRest(voice, rest);
                    break;
                case MeasureRestToken measureRest:
                    AddMeasureRest(voice, measureRest);
                    break;
                case BarLineToken bar:
                    CloseBar(voice, bar.Column);
                    break;
                case UnsupportedToken unsupported:
                    _diagnostics.Warning(
                        DiagnosticCodes.UnsupportedNotation,
                        $"Unsupported ABC notation '{unsupported.Text}' ({unsupported.Reason}) is ignored.",
                        _line,
                        unsupported.Column + 1);
                    break;
            }
        }

        private void AddChord(VoiceCursor voice, ChordSymbolToken chord)
        {
            if (!voice.Id.Equals(ChordVoiceId, StringComparison.Ordinal) && _voices.ContainsKey(ChordVoiceId))
            {
                _diagnostics.Warning(
                    DiagnosticCodes.ChordOutsideVocal,
                    $"Chord symbol \"{chord.Symbol}\" appears in voice '{voice.Id}'; YuE2 writes chords only in '{ChordVoiceId}'.",
                    _line,
                    chord.Column + 1);
            }

            _chords.Add(new PendingChord(voice.Position, chord.Symbol.Trim(), _line, chord.Column + 1));
        }

        private void AddNote(VoiceCursor voice, NoteToken note)
        {
            var ticks = ToTicks(note.Length, note.Column);
            if (ticks <= 0)
            {
                return;
            }

            var pitch = note.WrittenPitch
                        + voice.Accidentals.Resolve(note.Letter, note.Accidental, voice.Key.GetAlteration(note.Letter));

            if (voice.PendingTie is { } tie)
            {
                // An unmarked continuation keeps the tied pitch, even across a bar line.
                if (note.Accidental is null && note.WrittenPitch == tie.WrittenPitch)
                {
                    pitch = tie.Pitch;
                }

                if (pitch == tie.Pitch && voice.Notes.Count > 0)
                {
                    var tied = voice.Notes[^1];
                    voice.Notes[^1] = tied with { DurationTicks = tied.DurationTicks + ticks };
                    voice.Offset += ticks;
                    voice.PendingTie = note.Tied ? new TiedNote(pitch, note.WrittenPitch) : null;
                    return;
                }

                _diagnostics.Warning(
                    DiagnosticCodes.TieMismatch,
                    Invariant($"Tie joins different pitches ({tie.Pitch} and {pitch}); the second note gets its own attack."),
                    _line,
                    note.Column + 1);
            }

            if (pitch is < 0 or > 127)
            {
                _diagnostics.Warning(DiagnosticCodes.PitchOutOfRange, Invariant($"Pitch {pitch} is outside the MIDI range and is skipped."), _line, note.Column + 1);
                voice.PendingTie = null;
            }
            else
            {
                voice.Notes.Add(new NoteEvent(voice.Position, ticks, pitch));
                voice.PendingTie = note.Tied ? new TiedNote(pitch, note.WrittenPitch) : null;
            }

            voice.Offset += ticks;
        }

        private void AddRest(VoiceCursor voice, RestToken rest)
        {
            ReleaseTieIntoRest(voice, rest.Column);
            voice.Offset += Math.Max(0, ToTicks(rest.Length, rest.Column));
        }

        private void AddMeasureRest(VoiceCursor voice, MeasureRestToken rest)
        {
            if (rest.Measures < 1)
            {
                _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, "Measure rest with zero measures is ignored.", _line, rest.Column + 1);
                return;
            }

            ReleaseTieIntoRest(voice, rest.Column);
            if (voice.Offset != 0)
            {
                _diagnostics.Warning(DiagnosticCodes.BarLengthMismatch, "Full-measure rest inside a bar; the bar is closed first.", _line, rest.Column + 1);
                CloseBar(voice, rest.Column);
            }

            voice.Meter.TryGetMeasureTicks(_ppq, out var measureTicks);
            voice.Offset = rest.Measures * measureTicks;
            voice.MeasuresInBar = rest.Measures;
        }

        private void ReleaseTieIntoRest(VoiceCursor voice, int column)
        {
            if (voice.PendingTie is null)
            {
                return;
            }

            _diagnostics.Warning(DiagnosticCodes.TieMismatch, "A tie leads into a rest; the tie is dropped.", _line, column + 1);
            voice.PendingTie = null;
        }

        private void CloseBar(VoiceCursor voice, int? column, bool reportLength = true)
        {
            if (voice.Offset == 0)
            {
                return;
            }

            voice.Meter.TryGetMeasureTicks(_ppq, out var measureTicks);
            var expected = measureTicks * voice.MeasuresInBar;
            if (reportLength && voice.Offset != expected)
            {
                _diagnostics.Warning(
                    DiagnosticCodes.BarLengthMismatch,
                    Invariant($"Bar {voice.BarNumber} of voice '{voice.Id}' lasts {Quarters(voice.Offset)} quarter notes, expected {Quarters(expected)} in {voice.Meter}."),
                    _line == 0 ? null : _line,
                    column + 1);
            }

            voice.BarStart += Math.Max(voice.Offset, expected);
            voice.BarNumber += voice.MeasuresInBar;
            voice.Offset = 0;
            voice.MeasuresInBar = 1;
            voice.Accidentals.Reset();
        }

        private long ToTicks(AbcLength length, int column)
        {
            var unit = _unit!.Value;
            if (length.Numerator < 1 || length.Denominator < 1)
            {
                _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, $"Invalid note length '{length}' is ignored.", _line, column + 1);
                return 0;
            }

            if (!length.IsNative)
            {
                _diagnostics.Info(DiagnosticCodes.NonNativeDuration, $"Note length '{length}' is not produced by YuE2.", _line, column + 1);
            }

            var numerator = 4L * _ppq * unit.Numerator * length.Numerator;
            var denominator = (long)unit.Denominator * length.Denominator;
            if (numerator % denominator != 0)
            {
                _diagnostics.Warning(DiagnosticCodes.TickResolution, $"Note length '{length}' is rounded to the tick grid.", _line, column + 1);
            }

            return (long)Math.Round(numerator / (double)denominator, MidpointRounding.AwayFromZero);
        }

        // ---- Result ---------------------------------------------------------------------------

        private ScoreDocument BuildDocument()
        {
            var length = _voiceOrder.Max(v => v.Position);
            var timeSignatures = _meters.Distinct()
                .Select(e => new TimeSignatureChange(e.Tick, e.Value.Numerator, e.Value.Denominator))
                .ToArray();
            var keySignatures = _keys.Distinct()
                .Select(e => new KeySignatureChange(e.Tick, e.Value.Name, e.Value.Sharps, e.Value.IsMinor))
                .ToArray();
            var voices = _voiceOrder
                .Where(v => v.HasMusic)
                .Select(v => new VoiceTrack(v.Id, v.DisplayName, v.Notes.OrderBy(n => n.StartTicks).ToArray()))
                .ToArray();

            return new ScoreDocument(
                _ppq,
                string.IsNullOrWhiteSpace(_title) ? null : _title,
                _tempo!.Value,
                timeSignatures,
                keySignatures,
                _sections.ToArray(),
                voices,
                BuildChords(length),
                length);
        }

        /// <summary>A chord sounds until the next chord symbol, the next section, or the end of the score.</summary>
        private ChordEvent[] BuildChords(long scoreLength)
        {
            var ordered = _chords.OrderBy(c => c.Tick).ToList();
            var result = new List<ChordEvent>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var chord = ordered[i];
                if (i + 1 < ordered.Count && ordered[i + 1].Tick == chord.Tick)
                {
                    continue; // the later symbol at the same position wins
                }

                var end = scoreLength;
                if (i + 1 < ordered.Count)
                {
                    end = Math.Min(end, ordered[i + 1].Tick);
                }

                var nextSection = _sections.Find(s => s.StartTicks > chord.Tick);
                if (nextSection is not null)
                {
                    end = Math.Min(end, nextSection.StartTicks);
                }

                if (end <= chord.Tick)
                {
                    continue;
                }

                if (!ChordSymbolParser.TryParse(chord.Text, out var symbol))
                {
                    symbol = ChordSymbolParser.ParseRootOnly(chord.Text);
                    _diagnostics.Warning(
                        DiagnosticCodes.UnknownChord,
                        symbol is null
                            ? $"Chord symbol \"{chord.Text}\" is not recognized and is kept as text only."
                            : $"Chord symbol \"{chord.Text}\" is outside the YuE2 vocabulary and is played as a major triad.",
                        chord.Line,
                        chord.Column);
                }

                result.Add(new ChordEvent(chord.Tick, end - chord.Tick, chord.Text, symbol));
            }

            return result.ToArray();
        }

        // ---- Field value parsing --------------------------------------------------------------

        private AbcMeter? ParseMeter(string value)
        {
            if (AbcMeter.TryParse(value, out var meter))
            {
                return meter;
            }

            _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, $"Unsupported meter 'M:{value}' is ignored.", _line);
            return null;
        }

        private AbcLength? ParseUnit(string value)
        {
            var match = FractionPattern().Match(value);
            if (match.Success
                && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
                && int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
                && numerator > 0
                && denominator > 0)
            {
                return new AbcLength(numerator, denominator);
            }

            _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, $"Unsupported unit length 'L:{value}' is ignored.", _line);
            return null;
        }

        private bool IsWholeTickUnit(AbcLength unit) => 4L * _ppq * unit.Numerator % unit.Denominator == 0;

        /// <summary>Accepts <c>1/4=88</c>, <c>88</c> and other beat units such as <c>1/8=120</c> (converted to quarter notes).</summary>
        private double? ParseTempo(string value)
        {
            var match = TempoPattern().Match(value);
            if (match.Success
                && double.TryParse(match.Groups["bpm"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var bpm)
                && bpm > 0)
            {
                if (match.Groups["num"].Success)
                {
                    var beatNumerator = int.Parse(match.Groups["num"].ValueSpan, CultureInfo.InvariantCulture);
                    var beatDenominator = int.Parse(match.Groups["den"].ValueSpan, CultureInfo.InvariantCulture);
                    if (beatDenominator == 0)
                    {
                        _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, $"Unsupported tempo 'Q:{value}' is ignored.", _line);
                        return null;
                    }

                    bpm *= 4.0 * beatNumerator / beatDenominator;
                }

                return bpm;
            }

            _diagnostics.Warning(DiagnosticCodes.InvalidFieldValue, $"Unsupported tempo 'Q:{value}' is ignored.", _line);
            return null;
        }

        private AbcKey? ParseKey(string value, int? column = null)
        {
            if (AbcKey.TryParse(value, out var key))
            {
                return key;
            }

            _diagnostics.Warning(
                DiagnosticCodes.UnsupportedKey,
                $"Unsupported key '{value}' is ignored; only standard major and minor keys are supported.",
                _line,
                column + 1);
            return null;
        }

        private static (string Id, string? DisplayName) ParseVoiceField(string value)
        {
            var id = value.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var name = VoiceNamePattern().Match(value);
            return (id, name.Success ? name.Groups[1].Value : null);
        }

        private static string StripComment(string line)
        {
            var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                switch (line[i])
                {
                    case '"':
                        inQuotes = !inQuotes;
                        break;
                    case '%' when !inQuotes:
                        return line[..i];
                }
            }

            return line;
        }

        private string Quarters(long ticks) => (ticks / (double)_ppq).ToString("0.###", CultureInfo.InvariantCulture);

        private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

        [GeneratedRegex(@"^([A-Za-z]):(.*)$")]
        private static partial Regex FieldPattern();

        [GeneratedRegex(@"^\s*(\d+)\s*/\s*(\d+)\s*$")]
        private static partial Regex FractionPattern();

        [GeneratedRegex(@"(?:(?<num>\d+)\s*/\s*(?<den>\d+)\s*=\s*)?(?<bpm>\d+(?:\.\d+)?)\s*$")]
        private static partial Regex TempoPattern();

        [GeneratedRegex(@"\bname=""([^""]*)""")]
        private static partial Regex VoiceNamePattern();
    }

    private sealed record PendingChord(long Tick, string Text, int Line, int Column);

    /// <summary>Meter or key changes by tick. Body fields may override the header value at the same tick.</summary>
    private sealed class Timeline<T>
        where T : class
    {
        private readonly SortedDictionary<long, (T Value, bool FromBody)> _entries = [];

        public T Latest => _entries.Values.Last().Value;

        public void Set(long tick, T value, bool fromBody) => Set(tick, value, fromBody, out _);

        /// <returns><c>false</c> if another voice already set a different value at this tick.</returns>
        public bool Set(long tick, T value, bool fromBody, out T existing)
        {
            if (_entries.TryGetValue(tick, out var current) && current.FromBody && !current.Value.Equals(value))
            {
                existing = current.Value;
                return false;
            }

            _entries[tick] = (value, fromBody);
            existing = value;
            return true;
        }

        /// <summary>Entries in tick order, without changes that repeat the value already in effect.</summary>
        public IEnumerable<(long Tick, T Value)> Distinct()
        {
            T? previous = null;
            foreach (var (tick, entry) in _entries)
            {
                if (!entry.Value.Equals(previous))
                {
                    yield return (tick, entry.Value);
                }

                previous = entry.Value;
            }
        }
    }
}
