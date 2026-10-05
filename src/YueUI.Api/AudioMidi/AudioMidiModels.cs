namespace YueUI.Api.AudioMidi;

/// <param name="Python">Where the server looks for Basic Pitch's environment; the install script asks for it.</param>
public sealed record AudioMidiInfo(bool Installed, string Python);

/// <summary>How a track becomes MIDI.</summary>
/// <param name="Mono">One note at a time, as a voice sings: Basic Pitch is polyphonic and hears overtones as notes.</param>
/// <param name="Quantize">Note starts and ends on the sixteenth grid of the tempo; needs one.</param>
/// <param name="Bends">Keeps the pitch bends Basic Pitch hears (slides, vibrato); only with <see cref="Mono"/>.</param>
/// <param name="Tempo">
/// Written into the file so the bars in Logic line up with the recording. A stem takes its song's from the score;
/// an upload names it or gets 120 bpm.
/// </param>
public sealed record AudioMidiSettings(bool Mono = true, bool Quantize = false, bool Bends = false, double? Tempo = null)
{
    public const double MinTempo = 20;

    public const double MaxTempo = 300;

    public Dictionary<string, string[]> Validate(bool tempoKnown)
    {
        var errors = new Dictionary<string, string[]>();
        if (Tempo is { } tempo && (double.IsNaN(tempo) || tempo is < MinTempo or > MaxTempo))
        {
            errors["tempo"] = [$"Between {MinTempo} and {MaxTempo} bpm."];
        }
        if (Quantize && Tempo is null && !tempoKnown)
        {
            errors["quantize"] = ["The grid needs a tempo."];
        }
        return errors;
    }
}
