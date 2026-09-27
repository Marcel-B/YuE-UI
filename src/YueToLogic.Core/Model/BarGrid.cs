namespace YueToLogic.Core.Model;

/// <summary>
/// The bars of a score on a grid of whole units (sixteenths, the unit length of the YuE2 dialect), laid out from
/// its meter changes. A change that does not fall on a bar line takes effect at the next one: a bar in the
/// dialect never changes its meter halfway, and a MIDI file edited in a DAW may well say otherwise.
/// </summary>
internal sealed class BarGrid
{
    private readonly List<GridBar> _bars = [];

    /// <param name="meters">
    /// Meter changes by unit position, in order. The first one applies from the start wherever it stands; without
    /// any the score is in 4/4. Every meter must fill its bar with whole units.
    /// </param>
    /// <param name="length">Units the bars must cover; there is always at least one bar.</param>
    /// <param name="unitsPerWhole">Units in a whole note: 16 for sixteenths.</param>
    public BarGrid(IReadOnlyList<MeterAt> meters, long length, int unitsPerWhole = 16)
    {
        var current = meters.Count > 0 ? meters[0] : new MeterAt(0, 4, 4);
        var next = 1;
        var position = 0L;
        while (position < length || _bars.Count == 0)
        {
            while (next < meters.Count && meters[next].Start <= position)
            {
                current = meters[next++];
            }

            var barLength = (long)current.Numerator * unitsPerWhole / current.Denominator;
            var beat = Math.Max(1, unitsPerWhole / current.Denominator);
            _bars.Add(new GridBar(position, barLength, current.Numerator, current.Denominator, beat));
            position += barLength;
        }
    }

    public IReadOnlyList<GridBar> Bars => _bars;

    /// <summary>Where the last bar ends; at least the length the grid was built for.</summary>
    public long End => _bars[^1].End;

    /// <summary>The bar a position falls in; positions before the first bar give 0, after the last bar the last index.</summary>
    public int IndexAt(long position)
    {
        var low = 0;
        var high = _bars.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_bars[middle].Start <= position)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }
}

internal readonly record struct MeterAt(long Start, int Numerator, int Denominator);

/// <param name="Beat">Length of one beat of the meter in units, e.g. 4 in 4/4 and 2 in 6/8.</param>
internal readonly record struct GridBar(long Start, long Length, int Numerator, int Denominator, long Beat)
{
    public long End => Start + Length;

    public bool SameMeter(GridBar other) => Numerator == other.Numerator && Denominator == other.Denominator;
}

/// <summary>A note on the unit grid, from its first unit up to (not including) <see cref="End"/>.</summary>
internal readonly record struct GridNote(long Start, long End, int Pitch);
