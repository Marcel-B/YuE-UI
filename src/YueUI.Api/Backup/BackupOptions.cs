namespace YueUI.Api.Backup;

/// <summary>
/// The nightly backup to a Nextcloud (or any WebDAV server), configured under <c>Backup</c>. Without an address, a user
/// and a password it is off: <c>GET /api/backup</c> says so and the settings menu leaves it out.
/// </summary>
/// <remarks>
/// The password belongs in <c>appsettings.Production.json</c> on the Mac or in <see cref="AppPasswordFile"/>, never in
/// the repository. Like module-o-mat's backup, it is an app password (Nextcloud: Settings → Security → Devices &amp;
/// sessions), which can be revoked on its own and does not open the account's web login.
/// </remarks>
public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>
    /// The WebDAV folder the backup goes into, e.g.
    /// <c>https://cloud.example.de/remote.php/dav/files/marcel/Tonwerk</c>. The folder itself is created if missing,
    /// the one above it has to exist. A LAN address does not work from the LaunchAgent (macOS's local network
    /// permission); a Tailscale or public address does.
    /// </summary>
    public string? WebDavUrl { get; set; }

    public string? Username { get; set; }

    public string? AppPassword { get; set; }

    /// <summary>A file holding <see cref="AppPassword"/> instead; <c>~/</c> is the home folder.</summary>
    public string? AppPasswordFile { get; set; } = "~/.config/tonwerk/nextcloud-app-password";

    /// <summary>When the nightly backup starts, local time in <see cref="TimeZone"/>; empty for manual backups only.</summary>
    public string? At { get; set; } = "03:00";

    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>
    /// Whether the songs, versions, stems, covers, recorded voices, takes and transcriptions go along as files, or only
    /// the database. The first backup uploads all of them; later ones only what is new or changed.
    /// </summary>
    public bool Files { get; set; } = true;

    /// <summary>How long one upload may take; a stem set's FLAC over a slow upload takes minutes.</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A failed nightly backup is tried again after this long, not every minute.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Files written more recently than this may still be in the works and wait for the next backup.</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(2);

    public Uri? BaseUri =>
        Uri.TryCreate(WebDavUrl?.Trim() is { Length: > 0 } url ? url.TrimEnd('/') + "/" : null, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" ? uri : null;

    public string? ResolvedPassword
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AppPassword))
            {
                return AppPassword.Trim();
            }
            if (string.IsNullOrWhiteSpace(AppPasswordFile))
            {
                return null;
            }
            var path = AppPasswordFile.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPasswordFile[2..])
                : AppPasswordFile;
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

    public bool Configured => BaseUri is not null && !string.IsNullOrWhiteSpace(Username) && ResolvedPassword is not null;

    /// <summary>The daily start, or null for manual backups only (or a time that does not parse).</summary>
    public TimeOnly? DailyAt => TimeOnly.TryParse(At, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : null;

    public TimeZoneInfo Zone
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }
}
