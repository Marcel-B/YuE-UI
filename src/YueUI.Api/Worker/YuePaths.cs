namespace YueUI.Api.Worker;

/// <summary>
/// Where YuE Studio keeps its Python environment, models and songs. The defaults are the app's own locations,
/// so this server and the app share one installation and one song library.
/// </summary>
/// <remarks>
/// Configured under <c>Yue</c>: <c>InstallRoot</c> (default <c>~/Library/Application Support/YuE Studio</c>) and
/// <c>OutputDir</c> (default <c>~/Music/YuE Studio</c>, where the app writes its songs as well).
/// </remarks>
public sealed class YuePaths
{
    public YuePaths(string installRoot, string outputDir)
    {
        InstallRoot = Path.GetFullPath(installRoot);
        OutputDir = Path.GetFullPath(outputDir);
    }

    public string InstallRoot { get; }

    /// <summary>Run folders (<c>20260921-165850-Title/song1</c>) land here; the worker gets it as <c>YUE2_OUTPUT_DIR</c>.</summary>
    public string OutputDir { get; }

    public string Python => Path.Combine(InstallRoot, "env", "bin", "python");

    public string SourceRoot => Path.Combine(InstallRoot, "src");

    public string WorkerScript => Path.Combine(SourceRoot, "tools", "yue2_worker.py");

    /// <summary>
    /// The environment YuE Studio starts its worker with (read from the running app's worker process). Sharing the
    /// Neural Engine cache saves the minutes-long compile of programs the app already built.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment => new Dictionary<string, string>
    {
        ["YUE2_OUTPUT_DIR"] = OutputDir,
        ["YUE2_ANE_CACHE"] = Path.Combine(InstallRoot, "ane-cache"),
        ["HF_HOME"] = Path.Combine(InstallRoot, "models"),
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
            string.IsNullOrWhiteSpace(outputDir) ? Path.Combine(home, "Music", "YuE Studio") : outputDir);
    }
}
