namespace YueUI.Api.Data;

/// <summary>The app's own database, configured under <c>Data</c>.</summary>
public sealed class DataOptions
{
    public const string Section = "Data";

    /// <summary>
    /// The SQLite file. Default: <c>~/Library/Application Support/YuE UI/yueui.db</c> (on macOS; the local
    /// application data folder elsewhere), next to <c>push.json</c>.
    /// </summary>
    public string? Path { get; set; }

    public string ResolvedPath => string.IsNullOrWhiteSpace(Path)
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "yueui.db")
        : System.IO.Path.GetFullPath(Path);
}
