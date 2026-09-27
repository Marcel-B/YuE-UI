using YueToLogic.Core.Model;

namespace YueToLogic.Core.Abc;

/// <summary>Reading position and bar-local state of one ABC voice while a score is parsed.</summary>
internal sealed class VoiceCursor(string id, string displayName, int order, AbcMeter meter, AbcKey key)
{
    public string Id { get; } = id;

    public string DisplayName { get; set; } = displayName;

    /// <summary>Declaration order; returning to an earlier voice starts a new group of measures.</summary>
    public int Order { get; } = order;

    public AbcMeter Meter { get; set; } = meter;

    public AbcKey Key { get; set; } = key;

    public AccidentalTracker Accidentals { get; } = new();

    public long BarStart { get; set; }

    public long Offset { get; set; }

    /// <summary>1 for a normal bar; n for a bar consisting of a <c>Zn</c> multi-measure rest.</summary>
    public int MeasuresInBar { get; set; } = 1;

    public int BarNumber { get; set; } = 1;

    public bool HasMusic { get; set; }

    public TiedNote? PendingTie { get; set; }

    public List<NoteEvent> Notes { get; } = [];

    public long Position => BarStart + Offset;
}

/// <param name="WrittenPitch">Pitch before accidentals; an unmarked continuation with the same letter and octave keeps the tied pitch.</param>
internal readonly record struct TiedNote(int Pitch, int WrittenPitch);
