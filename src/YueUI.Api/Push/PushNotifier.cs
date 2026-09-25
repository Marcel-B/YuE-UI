using System.Text.Json;
using System.Threading.Channels;
using YueUI.Api.Worker;

namespace YueUI.Api.Push;

/// <summary>
/// Listens to the worker's state like a browser does and sends a push to every subscription when a song, a
/// transcription or a lyrics draft finishes, so a phone in a pocket hears about it.
/// </summary>
/// <remarks>
/// Only the change to finished counts: a snapshot marks what is already done, and a song rendered again becomes news
/// again once it runs. Cancelled songs stay silent, since whoever cancelled them knows. Sending is decoupled from
/// reading, so a slow push service never makes this subscriber fall behind and get dropped by <see cref="WorkerHost"/>.
/// </remarks>
public sealed class PushNotifier(WorkerHost host, PushStore store, IPushSender sender, ILogger<PushNotifier> logger) : BackgroundService
{
    public const string AppUrl = "/ui/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, bool> _finished = [];
    private readonly Channel<Notification> _outbox = Channel.CreateUnbounded<Notification>(new UnboundedChannelOptions { SingleReader = true });

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(WatchAsync(stoppingToken), DeliverAsync(stoppingToken));

    /// <summary>The payload the service worker (<c>ClientApp/public/sw.js</c>) shows.</summary>
    public static string Payload((string Title, string Body) text, string tag) =>
        JsonSerializer.Serialize(new { title = text.Title, body = text.Body, tag, url = AppUrl }, Json);

    private async Task WatchAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A dropped subscriber's channel completes; subscribing again starts from a fresh snapshot.
            using var subscription = host.Subscribe(checkStudio: false);
            Remember(subscription.Snapshot);
            try
            {
                await foreach (var item in subscription.Reader.ReadAllAsync(stoppingToken))
                {
                    Consider(item.Data);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Remember(StatusSnapshot snapshot)
    {
        foreach (var song in snapshot.Songs)
        {
            _finished[$"song:{song.Id}"] = song.Finished;
        }
        foreach (var transcription in snapshot.Transcriptions ?? [])
        {
            _finished[$"transcription:{transcription.Id}"] = transcription.Finished;
        }
        if (snapshot.Lyrics is { } lyrics)
        {
            _finished[$"lyrics:{lyrics.Id}"] = lyrics.Finished;
        }
    }

    private void Consider(object data)
    {
        switch (data)
        {
            case SongState song when Finishes($"song:{song.Id}", song.Finished) && song.Stage != "cancelled":
                Queue($"song:{song.Id}", language => PushTexts.Song(song, language));
                break;
            case TranscriptionState transcription when Finishes($"transcription:{transcription.Id}", transcription.Finished)
                && transcription.Stage != "cancelled":
                Queue($"transcription:{transcription.Id}", language => PushTexts.Transcription(transcription, language));
                break;
            case LyricsState lyrics when Finishes($"lyrics:{lyrics.Id}", lyrics.Finished):
                Queue("lyrics", language => PushTexts.Lyrics(lyrics, language));
                break;
        }
    }

    private bool Finishes(string key, bool finished)
    {
        var was = _finished.TryGetValue(key, out var before) && before;
        _finished[key] = finished;
        return finished && !was;
    }

    private void Queue(string tag, Func<string, (string Title, string Body)> text) => _outbox.Writer.TryWrite(new Notification(tag, text));

    private async Task DeliverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in _outbox.Reader.ReadAllAsync(stoppingToken))
            {
                foreach (var subscription in store.Subscriptions)
                {
                    var payload = Payload(notification.Text(subscription.Language), notification.Tag);
                    await SendAsync(subscription, payload, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(PushSubscriptionEntry subscription, string payload, CancellationToken cancellationToken)
    {
        try
        {
            if (await sender.SendAsync(subscription, payload, cancellationToken) == PushResult.Gone)
            {
                logger.LogInformation("Removing a push subscription its service no longer knows");
                store.Remove(subscription.Endpoint);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not send a push notification");
        }
    }

    /// <param name="Tag">A newer notification with the same tag replaces the older one on the phone.</param>
    /// <param name="Text">Title and body in a subscription's language.</param>
    private sealed record Notification(string Tag, Func<string, (string Title, string Body)> Text);
}
