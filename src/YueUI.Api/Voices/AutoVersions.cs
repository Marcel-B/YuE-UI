using YueUI.Api.Worker;

namespace YueUI.Api.Voices;

/// <summary>
/// Queues the version a song was asked for in the form (<see cref="SongState.Voice"/>) as soon as the song is ready,
/// so that the chain YuE2 → stems → Seed-VC → mix runs without a second tap in the library.
/// </summary>
/// <remarks>
/// Listens to the worker's state like <see cref="Push.PushNotifier"/>. The version then waits in
/// <see cref="VoiceConverter"/>'s queue for the memory like any other, i.e. until the batch's other songs are done
/// too. A render never carries a voice, so rendering a draft again does not make a second version. The voice lives
/// only in the song's state: a server restart ends the worker and with it the songs, so there is nothing to resume.
/// </remarks>
public sealed class AutoVersions(WorkerHost host, Status.StatusHub hub, VoiceConverter converter, ILogger<AutoVersions> logger) : BackgroundService
{
    /// <summary>Songs whose version was queued, so that a fresh snapshot after a dropped subscription does not queue it twice.</summary>
    private readonly HashSet<string> _queued = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            // A dropped subscriber's channel completes; subscribing again starts from a fresh snapshot.
            using var subscription = hub.Subscribe(checkStudio: false);
            foreach (var song in subscription.Snapshot.Songs)
            {
                Consider(song);
            }
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    if (item.Data is SongState song)
                    {
                        Consider(song);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Consider(SongState song)
    {
        if (song is not { Stage: "ready", Voice: { } voice } || !_queued.Add(song.Id))
        {
            return;
        }
        try
        {
            converter.Enqueue(song.Id, song.Title, new ReferenceVoice(voice.VoiceId, voice.VoiceLabel, 0, null), voice.ToRequest());
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not queue the version of {Song} with {Voice}", song.Id, voice.VoiceLabel);
            host.LogError($"{song.Id}: the version with {voice.VoiceLabel} could not be queued ({exception.Message})");
        }
    }
}
