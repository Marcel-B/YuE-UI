using System.Globalization;

namespace YueToLogic.Core.Harmony;

/// <summary>
/// Note names as the score and the user write them. MIDI note numbers are named the way Logic (and Yamaha)
/// name them, middle C (60) being C3, because that is what the user reads off Logic's piano roll and off the
/// manuals of most drum machines when looking up which note a drum sits on.
/// </summary>
public static class NoteNames
{
    /// <summary>The octave number of MIDI note 0 in Logic's naming, where 60 is C3.</summary>
    private const int LowestOctave = -2;

    private static readonly string[] SharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

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

    /// <summary>The name of a MIDI note in Logic's convention: 36 is C1, 60 is C3, 0 is C-2.</summary>
    public static string Name(int midiNote)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(midiNote);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(midiNote, 127);
        return SharpNames[midiNote % 12] + (LowestOctave + (midiNote / 12)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a MIDI note from a number (<c>36</c>) or a name in Logic's convention (<c>C1</c>, <c>F#1</c>,
    /// <c>Bb0</c>, <c>C-2</c>); the letter in either case, a flat always as a lower-case b, surrounding space ignored.
    /// </summary>
    public static bool TryParse(string? text, out int midiNote)
    {
        midiNote = 0;
        var span = text.AsSpan().Trim();
        if (span.Length == 0)
        {
            return false;
        }

        if (int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            midiNote = number;
            return number is >= 0 and <= 127;
        }

        var letter = char.ToUpperInvariant(span[0]);
        if (letter is < 'A' or > 'G')
        {
            return false;
        }

        var index = 1;
        var semitone = NaturalSemitone(letter);
        while (index < span.Length && span[index] is '#' or 'b')
        {
            semitone += span[index] == '#' ? 1 : -1;
            index++;
        }

        if (!int.TryParse(span[index..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var octave))
        {
            return false;
        }

        var note = ((octave - LowestOctave) * 12) + semitone;
        if (note is < 0 or > 127)
        {
            return false;
        }

        midiNote = note;
        return true;
    }
}
