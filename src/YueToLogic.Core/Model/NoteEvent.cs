namespace YueToLogic.Core.Model;

/// <param name="NoteNumber">MIDI note number, 60 = middle C (ABC <c>C</c>).</param>
/// <param name="Velocity">1–127, or <c>null</c> to use the renderer's default for the track.</param>
public sealed record NoteEvent(long StartTicks, long DurationTicks, int NoteNumber, int? Velocity = null);
