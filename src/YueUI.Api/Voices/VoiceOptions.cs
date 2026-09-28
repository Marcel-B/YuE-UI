namespace YueUI.Api.Voices;

/// <summary>
/// Where the voice chain runs, configured under <c>Voice</c>: ChangeMyVoice (github.com/Marcel-B/ChangeMyVoice,
/// Seed-VC) keeps the reference voices and converts the vocals, StemMyWav (github.com/Marcel-B/StemMyWav) separates
/// them from the song first. Both run on this Mac; their gateways on Proxmox are not needed from here.
/// </summary>
public sealed class VoiceOptions
{
    public const string Section = "Voice";

    /// <summary>
    /// ChangeMyVoice's Mac API, e.g. <c>http://100.93.85.52:5080</c> (it binds to its Tailscale address; that address
    /// has to be in its <c>AllowedClientAddresses</c>, since this server calls from the same Mac). Empty switches
    /// voices off: the endpoints answer 501 and the interface hides them.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>A key made with ChangeMyVoice's <c>scripts/neuer-zugang.sh</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>A file holding <see cref="ApiKey"/> instead, so the key need not stand in appsettings.</summary>
    public string? ApiKeyFile { get; set; }

    /// <summary>StemMyWav's Mac API, which <c>deploy/mac/start.sh</c> binds to this address.</summary>
    public string? StemsBaseUrl { get; set; } = "http://127.0.0.1:5081";

    public string? StemsApiKey { get; set; }

    /// <summary>Where StemMyWav's setup puts its key; read unless <see cref="StemsApiKey"/> is set.</summary>
    public string? StemsApiKeyFile { get; set; } = "~/.config/stemmywav/mac-api-key";

    /// <summary>
    /// The separation model, an id of StemMyWav's catalog. Mel-RoFormer Kim gives the cleanest vocals of the ones
    /// that take less time than the song lasts; artefacts left in the vocals go through Seed-VC as well.
    /// </summary>
    public string StemModel { get; set; } = "mel-roformer-kim-vocals";

    /// <summary>How often a running conversion is asked for its state.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a queued version looks whether YuE2 or the lyrics model has left the memory.</summary>
    public TimeSpan WaitInterval { get; set; } = TimeSpan.FromSeconds(10);

    public Uri? BaseUri => Parse(BaseUrl);

    public Uri? StemsBaseUri => Parse(StemsBaseUrl);

    public string? ResolvedApiKey => Key(ApiKey, ApiKeyFile);

    public string? ResolvedStemsApiKey => Key(StemsApiKey, StemsApiKeyFile);

    /// <summary>Reference voices can be managed.</summary>
    public bool VoicesConfigured => BaseUri is not null && ResolvedApiKey is not null;

    /// <summary>Songs can be split into stems on the voices page.</summary>
    public bool StemsConfigured => StemsBaseUri is not null && ResolvedStemsApiKey is not null;

    /// <summary>Songs can be sung with another voice: the voice service and the stem service are both there.</summary>
    public bool ConversionConfigured => VoicesConfigured && StemsConfigured;

    /// <summary>A relative path would resolve against the API's own address.</summary>
    private static Uri? Parse(string? url) =>
        Uri.TryCreate(url?.EndsWith('/') == false ? url + "/" : url, UriKind.Absolute, out var uri) ? uri : null;

    private static string? Key(string? key, string? file)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }
        var path = file.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[2..])
            : file;
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } read ? read : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
