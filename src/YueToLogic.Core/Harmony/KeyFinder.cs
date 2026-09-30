using YueToLogic.Core.Abc;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Harmony;

/// <summary>A major or minor key with the pitch classes of its scale (natural minor for minor keys).</summary>
internal sealed record ScaleKey(int TonicPitchClass, bool IsMinor)
{
    private static readonly int[] MajorSteps = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>Tonic of the major key with the same signature: the key itself, or the one a minor third up.</summary>
    public int MajorTonic => IsMinor ? (TonicPitchClass + 3) % 12 : TonicPitchClass;

    /// <summary>Sharps (positive) or flats (negative) of the signature; F# rather than Gb, and at most five flats.</summary>
    public int Sharps
    {
        get
        {
            // Each sharp moves the major tonic a fifth up: 7 * sharps = tonic (mod 12).
            var sharps = (MajorTonic * 7) % 12;
            return sharps > 6 ? sharps - 12 : sharps;
        }
    }

    public string Name => AbcKey.FromSignature(Sharps, IsMinor).Name;

    public bool Contains(int pitch) => Array.IndexOf(MajorSteps, Mod12(pitch - MajorTonic)) >= 0;

    /// <summary>
    /// The pitch <paramref name="steps"/> scale notes above (positive) or below (negative) <paramref name="pitch"/>;
    /// a note outside the scale counts from where it stands, so a raised leading tone still gets a neighbour.
    /// </summary>
    public int Step(int pitch, int steps)
    {
        var direction = Math.Sign(steps);
        var result = pitch;
        for (var taken = 0; taken < Math.Abs(steps);)
        {
            result += direction;
            if (Contains(result))
            {
                taken++;
            }
        }

        return result;
    }

    public static ScaleKey FromSignature(int sharps, bool isMinor)
    {
        var major = Mod12(sharps * 7);
        return new ScaleKey(isMinor ? Mod12(major - 3) : major, isMinor);
    }

    public static bool TryParse(string text, out ScaleKey key)
    {
        if (AbcKey.TryParse(text, out var abc))
        {
            key = FromSignature(abc.Sharps, abc.IsMinor);
            return true;
        }

        key = new ScaleKey(0, false);
        return false;
    }

    internal static int Mod12(int value) => ((value % 12) + 12) % 12;
}

/// <summary>
/// Finds the key of a melody with the Krumhansl-Schmuckler method: the duration each pitch class sounds is
/// correlated with the listening-test profiles of every major and minor key, and the best match wins.
/// </summary>
internal static class KeyFinder
{
    // Krumhansl and Kessler's probe-tone ratings, from the tonic up by semitones.
    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <returns>The best matching key, or <c>null</c> for no notes.</returns>
    public static ScaleKey? Find(IEnumerable<NoteEvent> notes)
    {
        var weights = new double[12];
        foreach (var note in notes)
        {
            weights[ScaleKey.Mod12(note.NoteNumber)] += note.DurationTicks;
        }

        if (weights.Sum() <= 0)
        {
            return null;
        }

        ScaleKey? best = null;
        var bestScore = double.NegativeInfinity;
        for (var tonic = 0; tonic < 12; tonic++)
        {
            foreach (var minor in new[] { false, true })
            {
                var score = Correlation(weights, minor ? MinorProfile : MajorProfile, tonic);
                if (score > bestScore + 1e-9)
                {
                    bestScore = score;
                    best = new ScaleKey(tonic, minor);
                }
            }
        }

        return best;
    }

    /// <summary>Share of the notes' duration that lies in the key's scale, 0 to 1.</summary>
    public static double Fit(ScaleKey key, IEnumerable<NoteEvent> notes)
    {
        double inside = 0, total = 0;
        foreach (var note in notes)
        {
            total += note.DurationTicks;
            if (key.Contains(note.NoteNumber))
            {
                inside += note.DurationTicks;
            }
        }

        return total > 0 ? inside / total : 1;
    }

    private static double Correlation(double[] weights, double[] profile, int tonic)
    {
        var meanWeight = weights.Average();
        var meanProfile = profile.Average();
        double covariance = 0, weightSpread = 0, profileSpread = 0;
        for (var pitchClass = 0; pitchClass < 12; pitchClass++)
        {
            var w = weights[pitchClass] - meanWeight;
            var p = profile[ScaleKey.Mod12(pitchClass - tonic)] - meanProfile;
            covariance += w * p;
            weightSpread += w * w;
            profileSpread += p * p;
        }

        return weightSpread == 0 ? 0 : covariance / Math.Sqrt(weightSpread * profileSpread);
    }
}
