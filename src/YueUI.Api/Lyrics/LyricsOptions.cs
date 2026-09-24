namespace YueUI.Api.Lyrics;

/// <summary>
/// Where lyrics are drafted: an OpenAI-compatible server, by default LM Studio on this Mac. Configured under
/// <c>Lyrics</c> (<c>Lyrics__Model</c> etc. as environment variables).
/// </summary>
public sealed class LyricsOptions
{
    public const string Section = "Lyrics";

    /// <summary>The server without <c>/v1</c>; LM Studio listens on port 1234.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";

    /// <summary>The model's id as <c>GET /v1/models</c> lists it.</summary>
    public string Model { get; set; } = "google/gemma-4-26b-a4b-qat";

    /// <summary>Only if "Require Authentication" is switched on in LM Studio's server settings.</summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// LM Studio's command line tool, used to start its server when nothing answers; default
    /// <c>~/.lmstudio/bin/lms</c>. Empty, or a missing file, leaves starting the server to the user.
    /// </summary>
    public string? Lms { get; set; }

    /// <summary>
    /// LM Studio unloads the model after this many idle seconds. The model is unloaded right after each draft
    /// anyway; this only covers a failed unload, so that it does not hold its memory for LM Studio's default hour.
    /// </summary>
    public int IdleTtlSeconds { get; set; } = 60;

    public double Temperature { get; set; } = 0.9;

    public string LmsPath => string.IsNullOrWhiteSpace(Lms)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms")
        : Lms;
}
