using YueToLogic.Core.Abc;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Tests;

internal static class TestScores
{
    public const int Ppq = 480;
    public const long Bar = 4 * Ppq;

    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Samples", "score.abc");

    /// <summary>A native YuE2 header followed by <paramref name="body"/>.</summary>
    public static string Native(string body, string meter = "4/4", string unit = "1/16", string key = "C", string tempo = "1/4=88") => $"""
        X:1
        T:
        M:{meter}
        L:{unit}
        Q:{tempo}
        V: Vocal clef=treble name="Vocal Melody" snm="Vocal"
        V: Ins clef=treble name="Ins Melody" snm="Inst."
        K:{key}
        {body}
        """;

    public static ScoreDocument ParseScore(string abc)
    {
        var result = new AbcScoreParser().Parse(abc);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Score!;
    }

    public static IReadOnlyList<Diagnostic> Warnings(this AbcParseResult result) =>
        result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();

    public static VoiceTrack Voice(this ScoreDocument score, string id) => score.Voices.Single(v => v.Id == id);

    public static int[] Pitches(this VoiceTrack voice) => voice.Notes.Select(n => n.NoteNumber).ToArray();
}
