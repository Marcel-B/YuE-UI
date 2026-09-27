using System.Buffers.Binary;
using YueToLogic.Core.Harmony;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Logic;

/// <summary>
/// The data record of a chord event on Logic's chord track (16 bytes, after the event's head record).
/// </summary>
/// <remarks>
/// Layout, derived from chords entered in Logic Pro 12.3:
/// bytes 0–1 bit mask of the chord's intervals above the root (bit n = n semitones),
/// bytes 2–3 slash bass as (spelling, pitch class) or 07 0F for none,
/// bytes 4–5 root as (spelling, pitch class), byte 13 root again as spelling·16 + pitch class,
/// bytes 14–15 the chord scale Session Players use, as a 12-bit mask relative to the root.
/// Spelling is 1 for a flat, 2 for a natural and 3 for a sharp (so Logic writes Gb, not F#).
/// </remarks>
internal static class LogicChordEncoding
{
    public const int RecordLength = 16;

    private const byte NoBassSpelling = 0x07;
    private const byte NoBassPitchClass = 0x0F;
    private const byte Natural = 2;

    /// <summary>The chord scales Logic chose for these qualities on a C root.</summary>
    private static readonly Dictionary<ChordQuality, ushort> Scales = new()
    {
        [ChordQuality.Major] = 0xAB5,
        [ChordQuality.Minor] = 0xAAD,
        [ChordQuality.Diminished] = 0x56D,
        [ChordQuality.Augmented] = 0xB35,
        [ChordQuality.Dominant7] = 0x6B5,
        [ChordQuality.Major7] = 0xAB5,
        [ChordQuality.Minor7] = 0x6AD,
        [ChordQuality.Diminished7] = 0x66B,
        [ChordQuality.HalfDiminished7] = 0x56D,
        [ChordQuality.Suspended4] = 0xAB5,
        [ChordQuality.Suspended2] = 0xAB5,
        [ChordQuality.Major6] = 0xAB5,
        [ChordQuality.Minor6] = 0xAAD,
        [ChordQuality.Dominant7Suspended4] = 0x6B5,
        [ChordQuality.MinorMajor7] = 0xAAD,
    };

    /// <summary>Fills <paramref name="record"/> (a copy of the template's record) for the given chord.</summary>
    public static void Encode(Span<byte> record, string text, ChordSymbol symbol)
    {
        var mask = ChordVoicing.GetIntervals(symbol.Quality).Aggregate(0, (bits, interval) => bits | (1 << interval));
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)mask);

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var root = Spell(slash < 0 ? text : text[..slash], symbol.RootPitchClass);
        if (symbol.BassPitchClass is { } bassPitchClass)
        {
            var bass = Spell(slash < 0 ? string.Empty : text[(slash + 1)..], bassPitchClass);
            record[2] = bass.Spelling;
            record[3] = bass.PitchClass;
        }
        else
        {
            record[2] = NoBassSpelling;
            record[3] = NoBassPitchClass;
        }

        record[4] = root.Spelling;
        record[5] = root.PitchClass;
        record[13] = (byte)((root.Spelling << 4) | root.PitchClass);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], Scales[symbol.Quality]);
    }

    /// <summary>
    /// Spelling of a note name at the start of <paramref name="name"/> ("Gb", "F#", "Ebb"); if there is none,
    /// the natural or sharp spelling of <paramref name="pitchClass"/>.
    /// </summary>
    private static (byte Spelling, byte PitchClass) Spell(string name, int pitchClass)
    {
        if (name.Length > 0 && name[0] is >= 'A' and <= 'G')
        {
            var accidentals = name.AsSpan(1).IndexOfAnyExcept('#', 'b');
            var accidental = accidentals < 0 ? name[1..] : name[1..(1 + accidentals)];
            if (accidental.Length <= 2)
            {
                var shift = accidental.Count(c => c == '#') - accidental.Count(c => c == 'b');
                return ((byte)(Natural + shift), (byte)pitchClass);
            }
        }

        var isNatural = pitchClass is 0 or 2 or 4 or 5 or 7 or 9 or 11;
        return ((byte)(isNatural ? Natural : Natural + 1), (byte)pitchClass);
    }
}
