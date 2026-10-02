namespace YueUI.Api.Voices;

/// <summary>
/// Where the voice chain runs, configured under <c>Voice</c>. This server runs the models itself when their
/// environments are installed (<c>deploy/setup-mac.sh --voices</c>): <c>mlx-audio-separator</c> separates the song
/// (<see cref="Separator"/>), Seed-VC converts the vocals (<see cref="SeedVcPython"/>, <see cref="SeedVcPath"/>), and
/// the reference voices are this server's own. Where they are not, it calls the services that did this before:
/// StemMyWav's Mac API (github.com/Marcel-B/StemMyWav) and ChangeMyVoice's (github.com/Marcel-B/ChangeMyVoice).
/// </summary>
public sealed class VoiceOptions
{
    public const string Section = "Voice";

    /// <summary>
    /// Where the separation and conversion environments live. Default: <c>engines</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/engines</c> on macOS), where <c>deploy/setup-mac.sh</c> puts them.
    /// </summary>
    public string? EngineRoot { get; set; }

    /// <summary>The <c>mlx-audio-separator</c> command; default <c>separator/env/bin/mlx-audio-separator</c> under <see cref="EngineRoot"/>.</summary>
    public string? Separator { get; set; }

    /// <summary>Where it keeps its models (<c>--model_file_dir</c>); default <c>separator/models</c> under <see cref="EngineRoot"/>.</summary>
    public string? SeparatorModels { get; set; }

    /// <summary>The model behind <c>dereverb</c>, which splits the separated vocals into dry vocals and their reverb.</summary>
    public string DereverbModel { get; set; } = "dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt";

    /// <summary>The Python with Seed-VC's packages; default <c>seed-vc/env/bin/python</c> under <see cref="EngineRoot"/>.</summary>
    public string? SeedVcPython { get; set; }

    /// <summary>A checkout of github.com/Plachtaa/seed-vc; default <c>seed-vc/src</c> under <see cref="EngineRoot"/>.</summary>
    public string? SeedVcPath { get; set; }

    /// <summary>Seed-VC's checkpoints (<c>HF_HUB_CACHE</c>), downloaded by the first conversion; default <c>seed-vc/models</c>.</summary>
    public string? SeedVcModels { get; set; }

    /// <summary>A conversion gives up after this long; a long song at 100 steps takes most of an hour.</summary>
    public TimeSpan ConversionTimeout { get; set; } = TimeSpan.FromHours(2);

    public string ResolvedEngineRoot => string.IsNullOrWhiteSpace(EngineRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "engines")
        : Expand(EngineRoot);

    public string ResolvedSeparator => Resolve(Separator, "separator", "env", "bin", "mlx-audio-separator");

    public string ResolvedSeparatorModels => Resolve(SeparatorModels, "separator", "models");

    public string ResolvedSeedVcPython => Resolve(SeedVcPython, "seed-vc", "env", "bin", "python");

    public string ResolvedSeedVcPath => Resolve(SeedVcPath, "seed-vc", "src");

    public string ResolvedSeedVcModels => Resolve(SeedVcModels, "seed-vc", "models");

    /// <summary>This server separates songs itself.</summary>
    public bool LocalStems => File.Exists(ResolvedSeparator);

    /// <summary>This server converts voices itself and keeps the reference voices.</summary>
    public bool LocalVoices => File.Exists(ResolvedSeedVcPython) && File.Exists(Path.Combine(ResolvedSeedVcPath, "inference.py"));

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

    /// <summary>ChangeMyVoice's API can be called, to convert or to take its voices over.</summary>
    public bool VoiceServiceConfigured => BaseUri is not null && ResolvedApiKey is not null;

    /// <summary>StemMyWav's API can be called.</summary>
    public bool StemServiceConfigured => StemsBaseUri is not null && ResolvedStemsApiKey is not null;

    /// <summary>Reference voices can be managed.</summary>
    public bool VoicesConfigured => LocalVoices || VoiceServiceConfigured;

    /// <summary>Songs can be split into stems on the voices page.</summary>
    public bool StemsConfigured => LocalStems || StemServiceConfigured;

    /// <summary>Songs can be sung with another voice: the voice service and the stem service are both there.</summary>
    public bool ConversionConfigured => VoicesConfigured && StemsConfigured;

    private string Resolve(string? configured, params string[] underRoot) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine([ResolvedEngineRoot, .. underRoot]) : Expand(configured);

    private static string Expand(string path) => Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path);

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
