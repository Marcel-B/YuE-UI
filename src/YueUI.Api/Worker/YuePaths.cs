namespace YueUI.Api.Worker;

/// <summary>
/// Where YuE Studio keeps its Python environment, models and songs. The defaults are the app's own locations,
/// so this server and the app share one installation and one song library.
/// </summary>
/// <remarks>
/// Configured under <c>Yue</c>: <c>InstallRoot</c> (default <c>~/Library/Application Support/YuE Studio</c>),
/// <c>OutputDir</c> (default <c>~/Music/YuE Studio</c>, where the app writes its songs as well) and
/// <c>SheetSagePython</c> (default: where YuE Studio's installer puts SheetSage2's environment).
/// </remarks>
public sealed class YuePaths
{
    private readonly string? _sheetSagePython;

    public YuePaths(string installRoot, string outputDir, string? sheetSagePython = null)
    {
        InstallRoot = Path.GetFullPath(installRoot);
        OutputDir = Path.GetFullPath(outputDir);
        _sheetSagePython = string.IsNullOrWhiteSpace(sheetSagePython) ? null : Path.GetFullPath(sheetSagePython);
    }

    public string InstallRoot { get; }

    /// <summary>Run folders (<c>20260921-165850-Title/song1</c>) land here; the worker gets it as <c>YUE2_OUTPUT_DIR</c>.</summary>
    public string OutputDir { get; }

    public string Python => Path.Combine(InstallRoot, "env", "bin", "python");

    public string SourceRoot => Path.Combine(InstallRoot, "src");

    public string WorkerScript => Path.Combine(SourceRoot, "tools", "yue2_worker.py");

    /// <summary>The worker writes a folder per transcription here (<c>{audio name}-{yyyyMMdd-HHmmss}</c>).</summary>
    public string TranscriptionsDir => Path.Combine(OutputDir, "transcriptions");

    public string ModelCache => Path.Combine(InstallRoot, "models");

    /// <summary>
    /// SheetSage2 runs in its own Python environment (its pins clash with YuE2's), which YuE Studio installs the
    /// first time someone transcribes in the app. The worker is told the path even before it exists, so an install
    /// made while the worker runs works without a restart.
    /// </summary>
    public string SheetSagePython =>
        _sheetSagePython ?? SheetSageCandidates.FirstOrDefault(File.Exists) ?? SheetSageCandidates[0];

    public bool SheetSageInstalled => File.Exists(SheetSagePython);

    private string[] SheetSageCandidates =>
    [
        // YuE Studio's installer (it also leaves sheetsage-installed.json next to it).
        Path.Combine(InstallRoot, "sheetsage-env", "bin", "python"),
        // The worker's own default, next to its sources.
        Path.Combine(SourceRoot, ".venv-sheetsage2", "bin", "python"),
    ];

    /// <summary>SheetSage2 and its MERT encoder are in the model cache, so transcribing needs no network.</summary>
    public bool SheetSageModelsCached =>
        new[] { "models--m-a-p--SheetSage2", "models--m-a-p--MERT-v2-FullSong" }
            .All(repo => Directory.Exists(Path.Combine(ModelCache, "hub", repo, "snapshots")));

    /// <summary>
    /// The environment YuE Studio starts its worker with (read from the running app's worker process). Sharing the
    /// Neural Engine cache saves the minutes-long compile of programs the app already built.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment => new Dictionary<string, string>
    {
        ["YUE2_OUTPUT_DIR"] = OutputDir,
        ["YUE2_ANE_CACHE"] = Path.Combine(InstallRoot, "ane-cache"),
        ["HF_HOME"] = ModelCache,
        ["YUE2_SHEETSAGE_PYTHON"] = SheetSagePython,
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["PYTHONUNBUFFERED"] = "1",
        ["TQDM_DISABLE"] = "1",
    };

    public static YuePaths FromConfiguration(IConfiguration configuration)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var section = configuration.GetSection("Yue");
        var installRoot = section["InstallRoot"];
        var outputDir = section["OutputDir"];
        return new YuePaths(
            string.IsNullOrWhiteSpace(installRoot) ? Path.Combine(home, "Library", "Application Support", "YuE Studio") : installRoot,
            string.IsNullOrWhiteSpace(outputDir) ? Path.Combine(home, "Music", "YuE Studio") : outputDir,
            section["SheetSagePython"]);
    }
}
