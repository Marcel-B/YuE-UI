using YueUI.Api.Backup;

namespace YueUI.Api;

/// <summary>The Nextcloud backup (<see cref="CloudBackup"/>): how the last one went, and one started by hand.</summary>
public static class BackupEndpoints
{
    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/backup", (CloudBackup backup) => backup.Status);

        api.MapPost("/backup", (CloudBackup backup) =>
        {
            if (!backup.Status.Configured)
            {
                return Results.Problem(title: "No Nextcloud is configured (NEXTCLOUD_WEBDAV_URL, NEXTCLOUD_USERNAME and NEXTCLOUD_APP_PASSWORD in ~/.config/tonwerk/nextcloud.env).", statusCode: StatusCodes.Status501NotImplemented);
            }
            return backup.Start()
                ? Results.Accepted("/api/backup", backup.Status)
                : Results.Problem(title: "A backup is already running.", statusCode: StatusCodes.Status409Conflict);
        });

        return api;
    }
}
