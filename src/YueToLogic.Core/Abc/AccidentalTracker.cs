namespace YueToLogic.Core.Abc;

/// <summary>
/// Bar-local accidentals with the YuE2/SheetSage2 convention: an accidental applies to its note
/// <em>letter in every octave</em> until the next bar line or key change. After <c>^F</c>, both
/// <c>F</c> and <c>f</c> are sharp. Standard ABC would only alter the same octave.
/// </summary>
internal sealed class AccidentalTracker
{
    private readonly Dictionary<char, int> _byLetter = [];

    /// <summary>Returns the alteration of a note and remembers explicit accidentals for the rest of the bar.</summary>
    public int Resolve(char upperLetter, int? explicitAlteration, int keyAlteration)
    {
        if (explicitAlteration is { } alteration)
        {
            _byLetter[upperLetter] = alteration;
            return alteration;
        }

        return _byLetter.TryGetValue(upperLetter, out var local) ? local : keyAlteration;
    }

    public void Reset() => _byLetter.Clear();
}
