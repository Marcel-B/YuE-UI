namespace YueUI.Api.Logic;

/// <summary>
/// Where Logic Pro projects are built: a yue-to-logic-pro server (github.com/Marcel-B/yue-to-logic-pro).
/// Configured under <c>Logic</c> (<c>Logic__BaseUrl</c> as an environment variable).
/// </summary>
public sealed class LogicOptions
{
    public const string Section = "Logic";

    /// <summary>
    /// The server without <c>/api</c>, e.g. <c>https://music.idsrv.info</c>. Empty switches the export off: the
    /// endpoint answers 501 and the library hides its button.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Fits the project's tempo to the length of the audio, so the MIDI regions do not drift away from the
    /// recording; yue-to-logic-pro leaves a score that is more than 5 % off as it is.
    /// </summary>
    public bool FitTempo { get; set; } = true;

    /// <summary>One region per song section (verse, chorus, …) instead of one per track.</summary>
    public bool SplitSections { get; set; }

    public Uri? BaseUri => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? uri : null;
}
