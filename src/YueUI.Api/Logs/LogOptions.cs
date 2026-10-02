namespace YueUI.Api.Logs;

/// <summary>The collected log, configured under <c>Logs</c>.</summary>
public sealed class LogOptions
{
    public const string Section = "Logs";

    /// <summary>
    /// Where the daily files go. Default: <c>logs</c> in the app's data folder
    /// (<c>~/Library/Application Support/YuE UI/logs</c> on macOS), next to the database.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>How many days of files are kept; older ones are deleted when a new day's file starts.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Above this a day's file takes only warnings and errors, so a chatty tool cannot fill the disk. A day of normal
    /// use is a few hundred kilobytes.
    /// </summary>
    public long MaxFileBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// <c>deploy/update.sh</c>'s log, read as the source <c>update</c>. Default:
    /// <c>~/Library/Logs/tonwerk-update.log</c>, where the script writes it.
    /// </summary>
    public string? UpdateLog { get; set; }

    public string ResolvedDirectory => string.IsNullOrWhiteSpace(Directory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YuE UI", "logs")
        : Path.GetFullPath(Directory);

    public string ResolvedUpdateLog => string.IsNullOrWhiteSpace(UpdateLog)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "tonwerk-update.log")
        : Path.GetFullPath(UpdateLog);
}
