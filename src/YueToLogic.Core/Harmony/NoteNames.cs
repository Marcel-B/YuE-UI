namespace YueToLogic.Core.Harmony;

internal static class NoteNames
{
    /// <summary>Semitone offset of a natural note letter above C.</summary>
    public static int NaturalSemitone(char upperLetter) => upperLetter switch
    {
        'C' => 0,
        'D' => 2,
        'E' => 4,
        'F' => 5,
        'G' => 7,
        'A' => 9,
        'B' => 11,
        _ => throw new ArgumentOutOfRangeException(nameof(upperLetter), upperLetter, "Expected a note letter A-G."),
    };

    /// <summary>Pitch class (0-11) of a note name such as <c>F#</c>, <c>Bb</c> or <c>Ebb</c>.</summary>
    public static int PitchClass(ReadOnlySpan<char> name)
    {
        var semitone = NaturalSemitone(name[0]);
        foreach (var accidental in name[1..])
        {
            semitone += accidental == '#' ? 1 : -1;
        }

        return ((semitone % 12) + 12) % 12;
    }
}
