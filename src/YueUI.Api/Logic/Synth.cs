using System.Text.Json;

namespace YueUI.Api.Logic;

/// <summary>
/// A named sound of the Logic page's browser synthesizer (oscillators, filter, envelopes, LFO). The patch is kept as
/// the JSON the browser holds and handed back as is, like a preset's form: a parameter added to the synthesizer needs
/// nothing here. It only shapes the preview in the browser, never the MIDI file or the Logic project.
/// </summary>
/// <param name="UpdatedAt">When the sound was last saved, ISO 8601 in UTC.</param>
public sealed record SynthPreset(long Id, string Name, JsonElement Patch, string UpdatedAt);

/// <summary>The sound a track plays in the preview, and the name of the preset it was taken from, if any.</summary>
public sealed record TrackSynth(string Track, JsonElement Patch, string? Preset, string UpdatedAt);

/// <summary>What a client sends to save a sound under a name; the name is in the URL.</summary>
public sealed record SynthPresetInput(JsonElement? Patch)
{
    public IReadOnlyList<string> Problems(string name)
    {
        var problems = new List<string>();
        SynthPatch.CheckName(name, "name", problems);
        SynthPatch.Check(Patch, problems);
        return problems;
    }
}

/// <summary>What a client sends to give a track its sound; the track is in the URL.</summary>
public sealed record TrackSynthInput(JsonElement? Patch, string? Preset)
{
    public IReadOnlyList<string> Problems(string track)
    {
        var problems = new List<string>();
        SynthPatch.CheckName(track, "track", problems);
        SynthPatch.Check(Patch, problems);
        if (Preset is not null && Preset.Trim().Length > SynthPatch.MaxNameLength)
        {
            problems.Add($"preset may have at most {SynthPatch.MaxNameLength} characters");
        }
        return problems;
    }
}

/// <summary>
/// The preview's mixer: volume and pan per track and the master, as the browser holds them. Only the browser reads them.
/// </summary>
/// <param name="Settings"><c>null</c> until the mixer was first saved.</param>
public sealed record MixerSettings(JsonElement? Settings, string? UpdatedAt);

/// <summary>What a client sends to save the mixer.</summary>
public sealed record MixerInput(JsonElement? Settings)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Settings is not { ValueKind: JsonValueKind.Object } value)
        {
            problems.Add("settings must be a JSON object");
        }
        else if (value.GetRawText().Length > SynthPatch.MaxPatchBytes)
        {
            problems.Add($"settings may have at most {SynthPatch.MaxPatchBytes} bytes");
        }
        return problems;
    }
}

public static class SynthPatch
{
    public const int MaxNameLength = 64;

    /// <summary>A patch is well under a kilobyte; anything near this is not one.</summary>
    public const int MaxPatchBytes = 16 * 1024;

    internal static void CheckName(string name, string what, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add($"{what} must not be empty");
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            problems.Add($"{what} may have at most {MaxNameLength} characters");
        }
    }

    internal static void Check(JsonElement? patch, List<string> problems)
    {
        if (patch is not { ValueKind: JsonValueKind.Object } value)
        {
            problems.Add("patch must be a JSON object");
        }
        else if (value.GetRawText().Length > MaxPatchBytes)
        {
            problems.Add($"patch may have at most {MaxPatchBytes} bytes");
        }
    }
}
