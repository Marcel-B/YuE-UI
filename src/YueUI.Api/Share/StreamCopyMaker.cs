using YueUI.Api.Library;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Share;

/// <summary>
/// Makes a song's or version's <see cref="StreamCopies">streaming copy</see> as soon as it is finished, so that its
/// first play on the road does not wait for the encoder. Listens to the worker's state like
/// <see cref="Push.PushNotifier"/>; <c>afconvert</c> takes a few seconds and little memory, so it does not wait for
/// the models.
/// </summary>
public sealed class StreamCopyMaker(WorkerHost host, SongLibrary library, StreamCopies streams) : BackgroundService
{
    /// <summary>What was made, so that a fresh snapshot after a dropped subscription does not queue it again.</summary>
    private readonly HashSet<string> _made = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            // A dropped subscriber's channel completes; subscribing again starts from a fresh snapshot.
            using var subscription = host.Subscribe(checkStudio: false);
            try
            {
                foreach (var song in subscription.Snapshot.Songs)
                {
                    await ConsiderAsync(song);
                }
                foreach (var version in subscription.Snapshot.Versions ?? [])
                {
                    await ConsiderAsync(version);
                }
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    await ConsiderAsync(item.Data);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ConsiderAsync(object? item)
    {
        switch (item)
        {
            // A render finishes the same song again with a new FLAC, hence the render in the key.
            case SongState { Stage: "ready" } song when song.Id.Split('/') is [var run, var name] && _made.Add($"song:{song.Id}:{song.Render}"):
                if (library.SongDirectory(run, name) is { } directory)
                {
                    await streams.SongAsync(run, name, Path.Combine(directory, "audio.flac"));
                }
                break;
            case VersionState { Stage: "done" } version when _made.Add($"version:{version.Id}"):
                await streams.VersionAsync(version.Id);
                break;
        }
    }
}
