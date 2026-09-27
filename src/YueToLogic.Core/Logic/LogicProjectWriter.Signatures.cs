using System.Buffers.Binary;
using System.Numerics;
using YueToLogic.Core.Model;

namespace YueToLogic.Core.Logic;

/// <summary>Logic's signature track: the meters and keys of the song.</summary>
/// <remarks>
/// The signature list holds both kinds of event, sorted by position. A meter is three 16-byte records, a key two.
/// Layout, derived from a project in which Logic Pro 12.3 was asked for E major, F minor, 3/4 and 6/8:
/// byte 0 of the first record is 0x30 for a meter and 0x32 for a key, bytes 4–7 its position; a meter carries
/// log2(denominator) in byte 11 and the numerator in byte 12, a key its number of accidentals in byte 12.
/// The events that start the song keep the positions of the template, where Logic stores them as 0.
/// </remarks>
public sealed partial class LogicProjectWriter
{
    private const int MeterEventLength = 48;
    private const int KeyEventLength = 32;

    /// <summary>Position of the event, in the first record and — for a meter — again in the second.</summary>
    private const int SignaturePositionOffset = 4;
    private const int MeterRepeatedPositionOffset = 16 + 12;

    /// <summary>Bar of a meter change in its second record, counted from zero.</summary>
    private const int MeterBarOffset = 16 + 8;

    /// <summary>Meter: numerator; key: accidentals, see <see cref="KeyValue"/>.</summary>
    private const int SignatureValueOffset = 12;
    private const int MeterDenominatorOffset = 11;
    private const int MeterTrailingValueOffset = 32 + 12;

    /// <summary>
    /// Set by Logic on meters such as 6/8, which it beats in two, and on the key of a chord region that a key
    /// change reaches.
    /// </summary>
    private const int SignatureFlagOffset = 15;
    private const byte SignatureFlag = 0x80;

    /// <summary>Key of a chord region, in the first record of its event list.</summary>
    private const int ChordRegionKeyOffset = SignatureValueOffset;
    private const int ChordRegionKeyFlagOffset = SignatureFlagOffset;

    /// <summary>Writes every meter and key change of the score to the signature track.</summary>
    private static void WriteSignatures(List<LogicChunk> chunks, ScoreDocument score)
    {
        var list = chunks.Single(c => c.Tag == "EvSq" && c.Class == SignatureListClass && c.Payload.Length > 16 && c.Payload[0] == 0x30);
        var meterTemplate = list.Payload.AsSpan(0, MeterEventLength).ToArray();
        var keyTemplate = list.Payload.AsSpan(MeterEventLength, KeyEventLength).ToArray();

        // Meters come before keys at the same position, as Logic writes them.
        var events = new List<(long Ticks, int Kind, byte[] Record)>();
        foreach (var meter in score.TimeSignatures)
        {
            var record = meterTemplate.ToArray();
            record[MeterDenominatorOffset] = (byte)BitOperations.Log2((uint)meter.Denominator);
            record[SignatureValueOffset] = (byte)meter.Numerator;
            record[SignatureFlagOffset] = IsCompound(meter) ? SignatureFlag : (byte)0;
            if (meter.StartTicks > 0)
            {
                var position = Position(score, meter.StartTicks);
                WriteUInt32(record, SignaturePositionOffset, position);
                WriteUInt32(record, MeterRepeatedPositionOffset, position);
                BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(MeterBarOffset), (short)(score.GetBarPosition(meter.StartTicks).Bar - 1));

                // In the meter that opens the song Logic keeps a value here that it leaves at zero in all others.
                record[MeterTrailingValueOffset] = 0;
            }

            events.Add((meter.StartTicks, 0, record));
        }

        foreach (var key in score.KeySignatures)
        {
            var record = keyTemplate.ToArray();
            record[SignatureValueOffset] = KeyValue(key);
            if (key.StartTicks > 0)
            {
                WriteUInt32(record, SignaturePositionOffset, Position(score, key.StartTicks));
            }

            events.Add((key.StartTicks, 1, record));
        }

        list.Payload = [.. events.OrderBy(e => e.Ticks).ThenBy(e => e.Kind).SelectMany(e => e.Record), .. SequenceTerminator];
    }

    /// <summary>The key a chord sounds in, which Logic uses to suggest a chord scale.</summary>
    private static void WriteChordRegionKey(Span<byte> events, ScoreDocument score, long ticks)
    {
        var key = KeyAt(score, ticks);
        if (key is null)
        {
            return;
        }

        events[ChordRegionKeyOffset] = KeyValue(key);
        events[ChordRegionKeyFlagOffset] = key.StartTicks > 0 ? SignatureFlag : (byte)0;
    }

    private static KeySignatureChange? KeyAt(ScoreDocument score, long ticks) =>
        score.KeySignatures.LastOrDefault(k => k.StartTicks <= ticks) ?? score.KeySignatures.FirstOrDefault();

    /// <summary>C major is 7, every sharp counts one more and every flat one less; a minor key adds 16.</summary>
    private static byte KeyValue(KeySignatureChange key) =>
        (byte)(7 + Math.Clamp(key.Sharps, -7, 7) + (key.IsMinor ? 16 : 0));

    /// <summary>6/8, 9/8 and 12/8 are beaten in groups of three eighths.</summary>
    private static bool IsCompound(TimeSignatureChange meter) =>
        meter.Denominator == 8 && meter.Numerator > 3 && meter.Numerator % 3 == 0;

    private static uint Position(ScoreDocument score, long ticks) =>
        checked((uint)(EventOrigin + ToLogicTicks(ticks, score.TicksPerQuarterNote)));
}
