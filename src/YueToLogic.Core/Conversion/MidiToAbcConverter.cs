using System.Globalization;
using YueToLogic.Core.Abc;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Midi;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Conversion;

/// <summary>
/// The way back: a MIDI file, typically exported from the Logic project this library wrote and edited there,
/// in; a YuE2 <c>score.abc</c> out, to generate the song again.
/// </summary>
public interface IMidiToAbcConverter
{
    MidiToAbcResult Convert(byte[] midi, MidiToAbcOptions? options = null);

    Task<MidiToAbcResult> ConvertAsync(Stream midi, MidiToAbcOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>What a track of the MIDI file becomes in the score.</summary>
public enum MidiTrackRole
{
    /// <summary>Left out: generated accompaniment such as bass, drums or a doubling.</summary>
    Ignore,

    /// <summary>The melody YuE2 sings, and the voice the chord symbols are written in.</summary>
    Vocal,

    /// <summary>The instrumental melody.</summary>
    Ins,

    /// <summary>Played chords, read back into chord symbols.</summary>
    Chords,
}

/// <summary>Serializable settings of the way back, e.g. as the body of a web request.</summary>
/// <remarks>Uses <c>set</c> for the reason given on <see cref="ConversionOptions"/>.</remarks>
public sealed record MidiToAbcOptions
{
    public const int MaxSkipBars = 999;

    /// <summary>
    /// The role of a track, by its <see cref="MidiTrackInfo.Index"/>. Tracks without an entry keep the role their
    /// name suggests; the result lists what every track became, so a host can show that and let it be changed.
    /// </summary>
    public IReadOnlyDictionary<int, MidiTrackRole> TrackRoles { get; set; } = new Dictionary<int, MidiTrackRole>();

    /// <summary>
    /// Bars left out at the start, 0 to <see cref="MaxSkipBars"/>. <c>null</c> leaves out the bars before the
    /// first note of a Vocal, Ins or Chords track, which is where a count-in sits.
    /// </summary>
    public int? SkipBars { get; set; }
}

/// <summary>A track of the MIDI file: a track chunk, or one channel of it if it plays on several.</summary>
/// <param name="Index">What <see cref="MidiToAbcOptions.TrackRoles"/> refers to it by.</param>
/// <param name="Channel">MIDI channel, 1-16.</param>
/// <param name="Polyphonic">Whether it mostly strikes several notes at once, as a chord track does.</param>
/// <param name="Role">What it became in this conversion.</param>
public sealed record MidiTrackInfo(int Index, string Name, int Channel, int NoteCount, bool Polyphonic, MidiTrackRole Role);

/// <param name="Abc">The <c>score.abc</c>, or <c>null</c> if no score could be made.</param>
/// <param name="Score">The score as written, i.e. quantized and with the count-in left out.</param>
/// <param name="Tracks">Every track with notes, also when conversion failed, so that roles can be assigned by hand.</param>
public sealed record MidiToAbcResult(
    bool Success,
    string? Abc,
    ScoreDocument? Score,
    IReadOnlyList<MidiTrackInfo> Tracks,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reads a Standard MIDI File back into a YuE2 score. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// The steps undo what <see cref="ScoreConverter"/> and the arranger did, as far as the dialect can say it:
/// <list type="number">
/// <item>Every track (every channel of a track that plays on several) is given a role by its name: the names the
/// MIDI file and the Logic project use (<c>Vocal</c>, <c>Ins</c>, <c>Chords</c>; a Logic track header adds
/// " · instrument", which is cut off), generated tracks and channel 10 are left out. Without names, a track that
/// strikes chords becomes Chords, the first other one Vocal and the next Ins.</item>
/// <item>Notes are moved to the nearest sixteenth, the unit of the dialect, which also undoes swing and humanizing
/// as far as they went.</item>
/// <item>Each voice plays one note at a time, as in YuE2's scores: of notes starting together the highest stays,
/// and a note ends where the next one begins.</item>
/// <item>The chord track is read back into symbols (<see cref="ChordTrackReader"/>); a file that has chord names
/// as text events on it but no notes, keeps those.</item>
/// <item>Silent bars at the start (a count-in) are left out, along with everything placed in them.</item>
/// </list>
/// YuE2 knows one tempo; the one at the start of the music is kept and rounded to whole BPM, as YuE2 writes it.
/// Markers become section comments at the bar line nearest to them.
/// </remarks>
public sealed class MidiToAbcConverter(IAbcScoreWriter writer) : IMidiToAbcConverter
{
    /// <summary>Resolution of the resulting <see cref="ScoreDocument"/>, the same as a converted score's.</summary>
    private const int Ppq = AbcParseOptions.DefaultTicksPerQuarterNote;

    private const int UnitsPerQuarter = 4;
    private const int UnitTicks = Ppq / UnitsPerQuarter;

    /// <summary>A note this far off the sixteenth grid (in sixteenths) counts as moved.</summary>
    private const double GridTolerance = 0.02;

    /// <summary>Share of a track's notes struck together with another one from which it counts as a chord track.</summary>
    private const double ChordalShare = 0.5;

    public MidiToAbcConverter()
        : this(new AbcScoreWriter())
    {
    }

    public MidiToAbcResult Convert(byte[] midi, MidiToAbcOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(midi);
        using var stream = new MemoryStream(midi, writable: false);
        return Convert(stream, options ?? new MidiToAbcOptions());
    }

    public async Task<MidiToAbcResult> ConvertAsync(Stream midi, MidiToAbcOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(midi);
        using var buffer = new MemoryStream();
        await midi.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return Convert(buffer, options ?? new MidiToAbcOptions());
    }

    private MidiToAbcResult Convert(Stream midi, MidiToAbcOptions options)
    {
        var diagnostics = new DiagnosticBag();
        if (!Validate(options, diagnostics))
        {
            return Failed([], diagnostics);
        }

        var content = MidiFileReader.Read(midi, diagnostics);
        if (content is null)
        {
            return Failed([], diagnostics);
        }

        var tracks = AssignRoles(content.Sources, options.TrackRoles);
        ReportRoles(tracks, diagnostics);

        var unit = content.TicksPerQuarterNote / (double)UnitsPerQuarter;
        var moved = 0;
        List<GridNote> Notes(MidiTrackRole role, bool count) => tracks
            .Where(t => t.Role == role)
            .SelectMany(t => content.Sources[t.Index].Notes)
            .Select(n =>
            {
                var start = (long)Math.Round(n.Start / unit, MidpointRounding.AwayFromZero);
                var end = Math.Max(start + 1, (long)Math.Round(n.End / unit, MidpointRounding.AwayFromZero));
                if (count && (Math.Abs((n.Start / unit) - start) > GridTolerance || Math.Abs((n.End / unit) - end) > GridTolerance))
                {
                    moved++;
                }

                return new GridNote(start, end, n.Pitch);
            })
            .ToList();

        var vocal = Notes(MidiTrackRole.Vocal, count: true);
        var ins = Notes(MidiTrackRole.Ins, count: true);
        var chordNotes = Notes(MidiTrackRole.Chords, count: false);
        var music = vocal.Concat(ins).Concat(chordNotes).ToList();
        if (music.Count == 0)
        {
            diagnostics.Error(DiagnosticCodes.MidiNoNotes, "No track read as Vocal, Ins or Chords has notes; choose which tracks those are.");
            return Failed(tracks, diagnostics);
        }

        if (moved > 0)
        {
            diagnostics.Info(DiagnosticCodes.MidiQuantized, Invariant($"{moved} notes were not on the sixteenth grid of the YuE2 dialect and were moved to the nearest sixteenth."));
        }

        // The grid of the whole file, to find the bars to leave out; the score then gets its own from there on.
        var meters = Meters(content, unit, diagnostics);
        var fileGrid = new BarGrid(meters, music.Max(n => n.End));
        var skipBars = options.SkipBars ?? fileGrid.IndexAt(music.Min(n => n.Start));
        var skip = skipBars < fileGrid.Bars.Count ? fileGrid.Bars[skipBars].Start : fileGrid.End;
        vocal = Shift(vocal, skip);
        ins = Shift(ins, skip);
        chordNotes = Shift(chordNotes, skip);
        if (vocal.Count + ins.Count + chordNotes.Count == 0)
        {
            diagnostics.Error(DiagnosticCodes.MidiNoNotes, Invariant($"Leaving out {skipBars} bars leaves no notes."));
            return Failed(tracks, diagnostics);
        }

        if (skipBars > 0)
        {
            diagnostics.Info(
                DiagnosticCodes.MidiBarsSkipped,
                options.SkipBars is null
                    ? Invariant($"The first {skipBars} bars have no notes (a count-in?) and were left out.")
                    : Invariant($"The first {skipBars} bars were left out."));
        }

        var length = new[] { vocal, ins, chordNotes }.SelectMany(n => n).Max(n => n.End);
        var grid = new BarGrid(ShiftMeters(meters, skip, diagnostics), length);
        var ticksAtSkip = (long)Math.Round(skip * unit);
        var keys = Keys(content, unit, skip, grid, diagnostics);
        vocal = Monophonic(vocal, AbcScoreWriter.VocalVoiceId, diagnostics);
        ins = Monophonic(ins, AbcScoreWriter.InsVoiceId, diagnostics);

        var score = new ScoreDocument(
            Ppq,
            null,
            Tempo(content, ticksAtSkip, diagnostics),
            TimeSignatures(grid),
            keys,
            Sections(content, unit, skip, grid),
            [
                new VoiceTrack(AbcScoreWriter.VocalVoiceId, AbcScoreWriter.VocalVoiceId, ToEvents(vocal)),
                new VoiceTrack(AbcScoreWriter.InsVoiceId, AbcScoreWriter.InsVoiceId, ToEvents(ins)),
            ],
            Chords(content, tracks, chordNotes, unit, skip, grid, keys, diagnostics),
            grid.End * UnitTicks);

        return new MidiToAbcResult(true, writer.Write(score), score, tracks, diagnostics.ToList());
    }

    private static bool Validate(MidiToAbcOptions options, DiagnosticBag diagnostics)
    {
        if (options.SkipBars is < 0 or > MidiToAbcOptions.MaxSkipBars)
        {
            diagnostics.Error(DiagnosticCodes.InvalidOption, Invariant($"skipBars must be between 0 and {MidiToAbcOptions.MaxSkipBars}, got {options.SkipBars}."));
        }

        foreach (var (index, role) in options.TrackRoles ?? new Dictionary<int, MidiTrackRole>())
        {
            if (!Enum.IsDefined(role))
            {
                diagnostics.Error(DiagnosticCodes.InvalidOption, Invariant($"Track {index} has no valid role ({(int)role})."));
            }
        }

        return !diagnostics.HasErrors;
    }

    private static MidiToAbcResult Failed(IReadOnlyList<MidiTrackInfo> tracks, DiagnosticBag diagnostics) =>
        new(false, null, null, tracks, diagnostics.ToList());

    // ---- Tracks -------------------------------------------------------------------------------

    private static List<MidiTrackInfo> AssignRoles(IReadOnlyList<MidiSource> sources, IReadOnlyDictionary<int, MidiTrackRole>? given)
    {
        var chordal = sources.Select(IsChordal).ToArray();
        var roles = sources.Select(RoleByName).ToArray();

        // Unnamed parts, as in a file from elsewhere: the chords are recognizable by their sound, the melodies by order.
        var unknown = Enumerable.Range(0, sources.Count).Where(i => roles[i] is null).ToList();
        if (!roles.Contains(MidiTrackRole.Chords) && unknown.FirstOrDefault(i => chordal[i], -1) is var chords and >= 0)
        {
            roles[chords] = MidiTrackRole.Chords;
            unknown.Remove(chords);
        }

        foreach (var role in new[] { MidiTrackRole.Vocal, MidiTrackRole.Ins })
        {
            if (!roles.Contains(role) && unknown.FirstOrDefault(i => !chordal[i], -1) is var melody and >= 0)
            {
                roles[melody] = role;
                unknown.Remove(melody);
            }
        }

        return sources
            .Select((source, i) => new MidiTrackInfo(
                i,
                source.Name,
                source.Channel,
                source.Notes.Count,
                chordal[i],
                given is not null && given.TryGetValue(i, out var chosen) ? chosen : roles[i] ?? MidiTrackRole.Ignore))
            .ToList();
    }

    /// <summary>
    /// The role the name of a track says, as this library names tracks in the MIDI file and in Logic. Logic's
    /// header reads "Bass · Mother32" once an instrument plays it, and a split track reads "Vocal (channel 2)".
    /// </summary>
    private static MidiTrackRole? RoleByName(MidiSource source)
    {
        if (source.Channel == 10)
        {
            return MidiTrackRole.Ignore;
        }

        var name = source.Name.Split('·', '(')[0].Trim().ToLowerInvariant();
        if (name.Contains("8vb", StringComparison.Ordinal) || name.Contains("8va", StringComparison.Ordinal) || name.Contains("15m", StringComparison.Ordinal))
        {
            return MidiTrackRole.Ignore;
        }

        return name switch
        {
            "vocal" or "vocals" or "vocal melody" or "lead vocal" or "voice" or "vox" or "gesang" => MidiTrackRole.Vocal,
            "ins" or "inst" or "inst." or "ins melody" or "instrument" or "instrumental" => MidiTrackRole.Ins,
            "chords" or "chord" or "akkorde" => MidiTrackRole.Chords,
            "bass" or "drums" or "guide" or "kick" or "snare" or "hihat" or "crash" or "click" => MidiTrackRole.Ignore,
            _ => null,
        };
    }

    private static bool IsChordal(MidiSource source) =>
        source.Notes.Count > 0
        && source.Notes.GroupBy(n => n.Start).Where(g => g.Count() > 1).Sum(g => g.Count()) >= ChordalShare * source.Notes.Count;

    private static void ReportRoles(IReadOnlyList<MidiTrackInfo> tracks, DiagnosticBag diagnostics)
    {
        foreach (var role in new[] { MidiTrackRole.Vocal, MidiTrackRole.Ins, MidiTrackRole.Chords })
        {
            var names = tracks.Where(t => t.Role == role).Select(t => $"'{t.Name}'").ToList();
            diagnostics.Info(
                DiagnosticCodes.MidiTrackRoles,
                names.Count == 0 ? $"No track is read as {role}." : $"{role}: {string.Join(", ", names)}.");
        }
    }

    // ---- Notes --------------------------------------------------------------------------------

    private static List<GridNote> Shift(List<GridNote> notes, long skip) =>
        notes
            .Where(n => n.End > skip)
            .Select(n => new GridNote(Math.Max(0, n.Start - skip), n.End - skip, n.Pitch))
            .ToList();

    /// <summary>One note at a time: of notes starting together the highest stays, and a note ends where the next begins.</summary>
    private static List<GridNote> Monophonic(List<GridNote> notes, string voice, DiagnosticBag diagnostics)
    {
        var result = new List<GridNote>();
        var dropped = 0;
        var shortened = 0;
        foreach (var note in notes.OrderBy(n => n.Start).ThenByDescending(n => n.Pitch))
        {
            if (result.Count > 0 && result[^1].Start == note.Start)
            {
                dropped++;
                continue;
            }

            if (result.Count > 0 && result[^1].End > note.Start)
            {
                result[^1] = result[^1] with { End = note.Start };
                shortened++;
            }

            result.Add(note);
        }

        if (dropped + shortened > 0)
        {
            diagnostics.Warning(
                DiagnosticCodes.MidiPolyphony,
                Invariant($"{voice} plays several notes at once, but a YuE2 voice plays one at a time: {dropped} notes under a higher one were left out, {shortened} notes were shortened to where the next one starts."));
        }

        return result;
    }

    private static NoteEvent[] ToEvents(List<GridNote> notes) =>
        [.. notes.Select(n => new NoteEvent(n.Start * UnitTicks, (n.End - n.Start) * UnitTicks, n.Pitch))];

    // ---- Chords -------------------------------------------------------------------------------

    private static ChordEvent[] Chords(
        MidiContent content,
        IReadOnlyList<MidiTrackInfo> tracks,
        List<GridNote> notes,
        double unit,
        long skip,
        BarGrid grid,
        IReadOnlyList<KeySignatureChange> keys,
        DiagnosticBag diagnostics)
    {
        IReadOnlyList<ChordChange> changes;
        if (notes.Count > 0)
        {
            var reading = ChordTrackReader.Read(notes, grid);
            changes = reading.Changes;
            if (reading.Unrecognized + reading.Incomplete > 0)
            {
                diagnostics.Warning(
                    DiagnosticCodes.MidiChords,
                    Invariant($"The chord track does not always play a chord of the YuE2 vocabulary: {reading.Unrecognized} beats formed no chord and keep the one before, {reading.Incomplete} chords were named from fewer notes than they have."));
            }
        }
        else
        {
            // Chord names as text, as the MIDI file of a conversion carries them next to the chord notes.
            changes = tracks
                .Where(t => t.Role == MidiTrackRole.Chords)
                .SelectMany(t => content.Sources[t.Index].Texts)
                .Select(t => (Start: (long)Math.Round(t.Tick / unit, MidpointRounding.AwayFromZero) - skip, t.Text))
                .Where(t => t.Start >= 0 && t.Start < grid.End)
                .Select(t => ChordSymbolParser.TryParse(t.Text, out var symbol) ? new ChordChange(t.Start, symbol) : (ChordChange?)null)
                .OfType<ChordChange>()
                .OrderBy(c => c.Start)
                .ToList();
            if (changes.Count == 0)
            {
                diagnostics.Warning(
                    DiagnosticCodes.MidiChords,
                    "No chord track with notes; the score has no chord symbols. Mark the track that plays the chords as Chords.");
            }
        }

        return
        [
            .. changes.Select((change, i) =>
            {
                var end = i + 1 < changes.Count ? changes[i + 1].Start : grid.End;
                var sharps = keys.LastOrDefault(k => k.StartTicks <= change.Start * UnitTicks)?.Sharps ?? 0;
                return new ChordEvent(change.Start * UnitTicks, (end - change.Start) * UnitTicks, ChordSymbolParser.Format(change.Symbol, sharps), change.Symbol);
            }).Where(c => c.DurationTicks > 0),
        ];
    }

    // ---- Tempo, meter, key, sections ----------------------------------------------------------

    /// <summary>The tempo at the start of the music, in whole BPM; YuE2 scores have one tempo.</summary>
    private static double Tempo(MidiContent content, long ticksAtSkip, DiagnosticBag diagnostics)
    {
        if (content.Tempos.Count == 0)
        {
            diagnostics.Info(DiagnosticCodes.MidiTempo, "The file sets no tempo; using the MIDI default of 120 BPM.");
            return 120;
        }

        var bpm = content.Tempos.LastOrDefault(t => t.Tick <= ticksAtSkip, content.Tempos[0]).Bpm;
        var changes = content.Tempos.Count(t => t.Tick > ticksAtSkip && Math.Abs(t.Bpm - bpm) > 0.01);
        var rounded = Math.Max(1, Math.Round(bpm, MidpointRounding.AwayFromZero));
        if (changes > 0)
        {
            diagnostics.Warning(DiagnosticCodes.MidiTempo, Invariant($"The tempo changes {changes} times, but a YuE2 score has one tempo; {rounded} BPM is kept."));
        }
        else if (Math.Abs(rounded - bpm) > 0.005)
        {
            diagnostics.Info(DiagnosticCodes.MidiTempo, Invariant($"The tempo of {bpm:0.###} BPM was rounded to {rounded}, as YuE2 writes it."));
        }

        return rounded;
    }

    /// <summary>The meters of the file in units; one the dialect cannot write (a bar that is no whole number of sixteenths) is left out.</summary>
    private static List<MeterAt> Meters(MidiContent content, double unit, DiagnosticBag diagnostics)
    {
        var meters = new List<MeterAt>();
        foreach (var meter in content.Meters)
        {
            if (meter.Numerator < 1 || meter.Denominator is < 1 or > 16 || 16 % meter.Denominator != 0)
            {
                diagnostics.Warning(DiagnosticCodes.MidiSignature, Invariant($"The meter {meter.Numerator}/{meter.Denominator} cannot be written with sixteenths and is left out."));
                continue;
            }

            var start = (long)Math.Round(meter.Tick / unit, MidpointRounding.AwayFromZero);
            meters.RemoveAll(m => m.Start == start);
            meters.Add(new MeterAt(start, meter.Numerator, meter.Denominator));
        }

        return meters;
    }

    /// <summary>The meters from the first bar kept on: the one in effect there opens the score.</summary>
    private static List<MeterAt> ShiftMeters(List<MeterAt> meters, long skip, DiagnosticBag diagnostics)
    {
        var shifted = new List<MeterAt>();
        var opening = meters.LastOrDefault(m => m.Start <= skip, meters.Count > 0 ? meters[0] : new MeterAt(0, 4, 4));
        shifted.Add(opening with { Start = 0 });
        shifted.AddRange(meters.Where(m => m.Start > skip).Select(m => m with { Start = m.Start - skip }));

        var grid = new BarGrid(shifted, shifted[^1].Start + 1);
        foreach (var change in shifted.Skip(1))
        {
            var bar = grid.Bars[grid.IndexAt(change.Start)];
            if (bar.Start != change.Start)
            {
                diagnostics.Warning(
                    DiagnosticCodes.MidiSignature,
                    Invariant($"The change to {change.Numerator}/{change.Denominator} falls inside bar {grid.IndexAt(change.Start) + 1} and takes effect with the next bar."));
            }
        }

        return shifted;
    }

    private static List<TimeSignatureChange> TimeSignatures(BarGrid grid)
    {
        var result = new List<TimeSignatureChange>();
        for (var i = 0; i < grid.Bars.Count; i++)
        {
            var bar = grid.Bars[i];
            if (i == 0 || !bar.SameMeter(grid.Bars[i - 1]))
            {
                result.Add(new TimeSignatureChange(bar.Start * UnitTicks, bar.Numerator, bar.Denominator));
            }
        }

        return result;
    }

    /// <summary>The keys from the first bar kept on, each at the start of the bar it falls in, since K: stands at a group start.</summary>
    private static List<KeySignatureChange> Keys(MidiContent content, double unit, long skip, BarGrid grid, DiagnosticBag diagnostics)
    {
        var keys = new List<KeySignatureChange>();
        var opening = content.Keys.LastOrDefault(k => k.Tick / unit <= skip + GridTolerance, content.Keys.Count > 0 ? content.Keys[0] : new MidiKey(0, 0, false));
        keys.Add(Change(0, opening));
        foreach (var key in content.Keys.Where(k => k.Tick / unit > skip + GridTolerance))
        {
            var start = (long)Math.Round(key.Tick / unit, MidpointRounding.AwayFromZero) - skip;
            if (start >= grid.End)
            {
                continue;
            }

            var index = grid.IndexAt(start);
            var bar = grid.Bars[index];
            if (bar.Start != start)
            {
                diagnostics.Warning(DiagnosticCodes.MidiSignature, Invariant($"The key change inside bar {index + 1} takes effect with the whole bar."));
            }

            keys.RemoveAll(k => k.StartTicks == bar.Start * UnitTicks);
            keys.Add(Change(bar.Start, key));
        }

        // A change to the key already in effect says nothing.
        return keys.Where((k, i) => i == 0 || k.Key != keys[i - 1].Key).ToList();

        static KeySignatureChange Change(long start, MidiKey key)
        {
            var abc = AbcKey.FromSignature(key.Sharps, key.IsMinor);
            return new KeySignatureChange(start * UnitTicks, abc.Name, abc.Sharps, abc.IsMinor);
        }
    }

    /// <summary>Markers as sections, each at the bar line nearest to it.</summary>
    private static List<SectionMarker> Sections(MidiContent content, double unit, long skip, BarGrid grid)
    {
        var sections = new List<SectionMarker>();
        foreach (var marker in content.Markers)
        {
            var start = (long)Math.Round(marker.Tick / unit, MidpointRounding.AwayFromZero) - skip;
            if (start < 0 || start >= grid.End)
            {
                continue;
            }

            var bar = grid.Bars[grid.IndexAt(start)];
            var line = start - bar.Start <= bar.End - start || bar.End >= grid.End ? bar.Start : bar.End;
            sections.Add(new SectionMarker(line * UnitTicks, marker.Text));
        }

        return sections;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
