using System.Globalization;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Arrangement;

public interface IScoreArranger
{
    ArrangementResult Arrange(ScoreDocument score, ArrangementOptions? options = null);
}

public sealed record ArrangementResult(ScoreDocument Score, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Transposes score voices by octaves and appends generated bass and drum tracks. The result is a new
/// <see cref="ScoreDocument"/>, so JSON output and MIDI file always show the same arrangement.
/// Stateless and thread-safe.
/// </summary>
public sealed class ScoreArranger : IScoreArranger
{
    public ArrangementResult Arrange(ScoreDocument score, ArrangementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        options ??= new ArrangementOptions();
        var diagnostics = new DiagnosticBag();

        var tracks = ShiftOctaves(score.Voices, options, diagnostics);
        if (options.Bass is { } bass)
        {
            tracks.Add(BassLineGenerator.Generate(score, bass));
        }

        if (options.Drums is { } drums)
        {
            tracks.Add(DrumPatternGenerator.Generate(score, drums));
        }

        return new ArrangementResult(score with { Voices = tracks }, diagnostics.ToList());
    }

    private static List<VoiceTrack> ShiftOctaves(
        IReadOnlyList<VoiceTrack> voices,
        ArrangementOptions options,
        DiagnosticBag diagnostics)
    {
        var melodyIds = voices.Where(v => v.Kind == TrackKind.Melody).Select(v => v.Id).ToList();
        foreach (var voiceId in options.OctaveShifts.Keys)
        {
            if (!melodyIds.Exists(id => id.Equals(voiceId, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Warning(
                    DiagnosticCodes.UnknownVoice,
                    $"Cannot transpose voice '{voiceId}': the score has no such voice (available: {string.Join(", ", melodyIds)}).");
            }
        }

        return voices.Select(voice => voice.Kind == TrackKind.Melody
                ? Shift(voice, OctavesFor(voice.Id, options), diagnostics)
                : voice)
            .ToList();
    }

    private static int OctavesFor(string voiceId, ArrangementOptions options)
    {
        foreach (var (id, octaves) in options.OctaveShifts)
        {
            if (id.Equals(voiceId, StringComparison.OrdinalIgnoreCase))
            {
                return octaves;
            }
        }

        return options.DefaultOctaveShift;
    }

    private static VoiceTrack Shift(VoiceTrack voice, int octaves, DiagnosticBag diagnostics)
    {
        if (octaves == 0)
        {
            return voice;
        }

        var semitones = octaves * 12;
        var shifted = voice.Notes
            .Where(n => n.NoteNumber + semitones is >= 0 and <= 127)
            .Select(n => n with { NoteNumber = n.NoteNumber + semitones })
            .ToArray();

        var dropped = voice.Notes.Count - shifted.Length;
        if (dropped > 0)
        {
            diagnostics.Warning(
                DiagnosticCodes.PitchOutOfRange,
                string.Create(CultureInfo.InvariantCulture, $"Transposing voice '{voice.Id}' by {octaves} octave(s) moves {dropped} note(s) outside the MIDI range; they are left out."));
        }

        return voice with { Notes = shifted };
    }
}
