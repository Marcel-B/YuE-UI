namespace YueUI.Api.AudioMidi;

/// <summary>
/// Audio to MIDI with Spotify's Basic Pitch, configured under <c>AudioMidi</c>. It needs its own Python environment,
/// which <c>deploy/install-midi.sh</c> makes; without it the page explains how and the endpoints that convert answer 501.
/// </summary>
public sealed class AudioMidiOptions
{
    public const string Section = "AudioMidi";

    /// <summary>
    /// Where the environment lives. Default: <c>midi</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/midi</c> on macOS), where the install script puts it too.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The Python with Basic Pitch; default <c>env/bin/python</c> under <see cref="Root"/>.</summary>
    public string? Python { get; set; }

    /// <summary>A three-minute track takes seconds; a long recording on a busy Mac a few minutes at most.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    public string ResolvedRoot => string.IsNullOrWhiteSpace(Root)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "midi")
        : Expand(Root);

    public string ResolvedPython => string.IsNullOrWhiteSpace(Python) ? Path.Combine(ResolvedRoot, "env", "bin", "python") : Expand(Python);

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);
}
