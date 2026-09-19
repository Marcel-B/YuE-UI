using System.Diagnostics.CodeAnalysis;

namespace YueToLogic.Core.Abc;

/// <summary>A standard major or minor key as used in <c>K:</c> fields.</summary>
internal sealed record AbcKey(string Name, int Sharps, bool IsMinor)
{
    private const string SharpOrder = "FCGDAEB";
    private const string FlatOrder = "BEADGCF";

    private static readonly Dictionary<string, int> KnownKeys = BuildKnownKeys();

    public static readonly AbcKey CMajor = new("C", 0, false);

    /// <summary>Alteration in semitones the key signature applies to a note letter.</summary>
    public int GetAlteration(char upperLetter)
    {
        if (Sharps > 0)
        {
            var index = SharpOrder.IndexOf(upperLetter, StringComparison.Ordinal);
            return index >= 0 && index < Sharps ? 1 : 0;
        }

        if (Sharps < 0)
        {
            var index = FlatOrder.IndexOf(upperLetter, StringComparison.Ordinal);
            return index >= 0 && index < -Sharps ? -1 : 0;
        }

        return 0;
    }

    /// <summary>
    /// Accepts <c>C</c>, <c>F#</c>, <c>Bb</c>, <c>Am</c>, <c>F#m</c> and the spelled-out forms
    /// <c>Amin</c>, <c>A minor</c>, <c>Cmaj</c>. Clef and other <c>name=value</c> attributes are ignored.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out AbcKey? key)
    {
        key = null;
        var compact = string.Concat(text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.Contains('=', StringComparison.Ordinal)));
        if (compact.Length == 0 || char.ToUpperInvariant(compact[0]) is < 'A' or > 'G')
        {
            return false;
        }

        var rootLength = compact.Length > 1 && compact[1] is '#' or 'b' ? 2 : 1;
        var root = char.ToUpperInvariant(compact[0]) + compact[1..rootLength];
        bool isMinor;
        switch (compact[rootLength..].ToLowerInvariant())
        {
            case "" or "maj" or "major" or "ion" or "ionian":
                isMinor = false;
                break;
            case "m" or "min" or "minor" or "aeo" or "aeolian":
                isMinor = true;
                break;
            default:
                return false;
        }

        var name = isMinor ? root + "m" : root;
        if (!KnownKeys.TryGetValue(name, out var sharps))
        {
            return false;
        }

        key = new AbcKey(name, sharps, isMinor);
        return true;
    }

    public override string ToString() => Name;

    private static Dictionary<string, int> BuildKnownKeys()
    {
        string[] major = ["Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"];
        string[] minor = ["Abm", "Ebm", "Bbm", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F#m", "C#m", "G#m", "D#m", "A#m"];
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < major.Length; i++)
        {
            keys[major[i]] = i - 7;
            keys[minor[i]] = i - 7;
        }

        return keys;
    }
}
