using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <summary>Parses chord symbols of the YuE2 vocabulary, e.g. <c>C</c>, <c>F#m7/C#</c>, <c>Gm6/Bb</c>, <c>Dm(maj7)</c>.</summary>
public static partial class ChordSymbolParser
{
    private static readonly Dictionary<string, ChordQuality> Qualities = new(StringComparer.Ordinal)
    {
        [""] = ChordQuality.Major,
        ["m"] = ChordQuality.Minor,
        ["dim"] = ChordQuality.Diminished,
        ["aug"] = ChordQuality.Augmented,
        ["7"] = ChordQuality.Dominant7,
        ["maj7"] = ChordQuality.Major7,
        ["m7"] = ChordQuality.Minor7,
        ["dim7"] = ChordQuality.Diminished7,
        ["m7b5"] = ChordQuality.HalfDiminished7,
        ["sus4"] = ChordQuality.Suspended4,
        ["sus2"] = ChordQuality.Suspended2,
        ["6"] = ChordQuality.Major6,
        ["m6"] = ChordQuality.Minor6,
        ["7sus4"] = ChordQuality.Dominant7Suspended4,
        ["m(maj7)"] = ChordQuality.MinorMajor7,
    };

    [GeneratedRegex(@"^(?<root>[A-G](?:bb|##|b|#)?)(?<quality>m\(maj7\)|maj7|m7b5|7sus4|dim7|sus4|sus2|dim|aug|m7|m6|m|7|6)?(?:/(?<bass>[A-G](?:bb|##|b|#)?))?$")]
    private static partial Regex ChordPattern();

    [GeneratedRegex(@"^(?<root>[A-G](?:bb|##|b|#)?)")]
    private static partial Regex RootPattern();

    /// <summary>Parses a symbol that belongs to the native vocabulary.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out ChordSymbol? chord)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = ChordPattern().Match(text.Trim());
        if (!match.Success)
        {
            chord = null;
            return false;
        }

        var bass = match.Groups["bass"];
        chord = new ChordSymbol(
            NoteNames.PitchClass(match.Groups["root"].ValueSpan),
            Qualities[match.Groups["quality"].Value],
            bass.Success ? NoteNames.PitchClass(bass.ValueSpan) : null);
        return true;
    }

    /// <summary>
    /// Fallback for symbols outside the vocabulary (e.g. <c>Cmaj9</c>): keeps the root and treats the chord as major.
    /// Returns <c>null</c> if not even a root can be recognized.
    /// </summary>
    public static ChordSymbol? ParseRootOnly(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = RootPattern().Match(text.Trim());
        return match.Success
            ? new ChordSymbol(NoteNames.PitchClass(match.Groups["root"].ValueSpan), ChordQuality.Major, null)
            : null;
    }
}
