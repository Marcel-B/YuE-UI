using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using YueUI.Api.Library;

namespace YueUI.Api.Worker;

/// <summary>An event for the browsers: <see cref="Type"/> becomes the SSE event name, <see cref="Data"/> its JSON.</summary>
public sealed record ServerEvent(string Type, object Data);

/// <summary>
/// Owns the one worker process and turns its event stream into state: the songs in flight, a log, and whether the
/// worker is up. Every change goes out to the subscribers (the browsers' event streams).
/// </summary>
/// <remarks>
/// The worker starts with the first command rather than with the server, so an idle server costs nothing; the
/// worker itself drops the model after ten idle minutes. Its protocol is documented at the top of
/// <c>yue2_worker.py</c>: songs are addressed by the absolute path of their <c>audio.flac</c>, which is mapped to
/// the library's <c>run/songN</c> ids here.
/// </remarks>
public sealed class WorkerHost(
    IWorkerLauncher launcher,
    SongLibrary library,
    IStudioDetector studio,
    TimeProvider time,
    ILogger<WorkerHost> logger) : IHostedService, IAsyncDisposable
{
    private const int LogCapacity = 300;
    private const int FinishedCapacity = 30;
    private const int SubscriberCapacity = 1000;

    /// <summary>The worker reports progress for every step; browsers (phones on mobile data) need far fewer.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan StudioCheckInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<string, SongState> _songs = [];
    private readonly Dictionary<string, DateTimeOffset> _lastProgress = [];
    private readonly LinkedList<LogEntry> _log = [];
    private readonly List<Channel<ServerEvent>> _subscribers = [];
    private IWorkerConnection? _connection;
    private WorkerStatus _status = WorkerStatus.Stopped;
    private string? _lastError;
    private bool? _extensions;
    private bool _studioRunning;
    private DateTimeOffset _studioCheckedAt = DateTimeOffset.MinValue;

    public StatusSnapshot Snapshot()
    {
        var studioRunning = StudioRunning();
        lock (_gate)
        {
            return SnapshotLocked(studioRunning);
        }
    }

    /// <summary>A snapshot and every change after it, with nothing lost in between. Dispose to unsubscribe.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(SubscriberCapacity) { SingleReader = true });
        var studioRunning = StudioRunning();
        lock (_gate)
        {
            _subscribers.Add(channel);
            return new Subscription(SnapshotLocked(studioRunning), channel.Reader, () => Unsubscribe(channel));
        }
    }

    public Task GenerateAsync(JsonObject command, CancellationToken cancellationToken) => SendAsync(command, cancellationToken);

    /// <summary>Synthesizes a song again from its saved tokens, e.g. a draft at full quality.</summary>
    public Task RenderAsync(string songDirectory, string quality, string? engines, CancellationToken cancellationToken)
    {
        var command = new JsonObject { ["cmd"] = "render", ["path"] = songDirectory, ["quality"] = quality };
        if (engines is not null)
        {
            command["engines"] = engines;
        }
        return SendAsync(command, cancellationToken);
    }

    /// <returns>False when the song is not in flight.</returns>
    public async Task<bool> CancelAsync(string id, CancellationToken cancellationToken)
    {
        string path;
        lock (_gate)
        {
            if (_connection is null || !_songs.TryGetValue(id, out var song) || song.Finished)
            {
                return false;
            }
            path = song.AudioPath;
        }
        await SendAsync(new JsonObject { ["cmd"] = "cancel", ["path"] = path }, cancellationToken);
        return true;
    }

    /// <summary>Cancels every song. Does not start a worker just for that.</summary>
    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is not null)
        {
            await connection.SendAsync("""{"cmd": "stop"}""", cancellationToken);
        }
    }

    /// <summary>Ends the worker process and with it all the memory the model holds (for when YuE Studio should run).</summary>
    public async Task ShutdownWorkerAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            var connection = _connection;
            if (connection is not null)
            {
                AddLog("info", "Stopping the worker");
                // The pump notices the end of the output and marks what was still running as failed.
                await connection.DisposeAsync();
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task SendAsync(JsonObject command, CancellationToken cancellationToken)
    {
        var connection = await EnsureStartedAsync(cancellationToken);
        await connection.SendAsync(command.ToJsonString(), cancellationToken);
    }

    private async Task<IWorkerConnection> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { } running)
            {
                return running;
            }

            var connection = launcher.Launch();
            lock (_gate)
            {
                _connection = connection;
                _status = WorkerStatus.Starting;
                _lastError = null;
            }
            PublishWorker();
            // Commands sent before "ready" wait in the pipe: the worker reads stdin only once it is up.
            _ = Task.Run(() => PumpAsync(connection), CancellationToken.None);
            return connection;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task PumpAsync(IWorkerConnection connection)
    {
        try
        {
            await foreach (var line in connection.Output.ReadAllAsync())
            {
                try
                {
                    Handle(line);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not handle worker line {Line}", line.Text);
                }
            }
        }
        finally
        {
            Exited(connection);
        }
    }

    private void Exited(IWorkerConnection connection)
    {
        var failed = new List<SongState>();
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection))
            {
                return;
            }
            _connection = null;
            _status = WorkerStatus.Stopped;
            foreach (var song in _songs.Values.Where(s => !s.Finished).ToList())
            {
                var updated = song with { Stage = "failed", Message = "The worker stopped.", UpdatedAt = time.GetUtcNow() };
                _songs[song.Id] = updated;
                failed.Add(updated);
            }
        }
        foreach (var song in failed)
        {
            Publish("song", song);
        }
        PublishWorker();
    }

    private void Handle(WorkerLine line)
    {
        if (line.IsError || !line.Text.StartsWith('{'))
        {
            // Python warnings and tracebacks; the worker's own messages come as "log" events.
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                AddLog(line.IsError ? "stderr" : "info", line.Text);
            }
            return;
        }

        JsonObject? message;
        try
        {
            message = JsonNode.Parse(line.Text) as JsonObject;
        }
        catch (JsonException)
        {
            message = null;
        }
        if (message is null)
        {
            AddLog("info", line.Text);
            return;
        }

        switch (Text(message["event"]))
        {
            case "ready":
                lock (_gate)
                {
                    _status = WorkerStatus.Ready;
                    // YuE Studio's worker started without the extension says nothing about it.
                    _extensions = message["yueui_extensions"] is JsonValue flag && flag.TryGetValue(out bool active) && active;
                }
                PublishWorker();
                break;
            case "log":
                AddLog("info", Text(message["message"]) ?? "");
                break;
            case "error":
                var error = Text(message["message"]) ?? "Unknown error";
                lock (_gate)
                {
                    _lastError = error;
                }
                AddLog("error", error);
                PublishWorker();
                break;
            case "started":
                Started(message);
                break;
            case "stage":
                Update(message, song => song with
                {
                    Stage = Text(message["stage"]) ?? song.Stage,
                    Detail = Text(message["detail"]) ?? "",
                    Engine = Text(message["engine"]) ?? song.Engine,
                    // A new stage starts from zero; the worker's progress events follow.
                    Fraction = Text(message["stage"]) == "ready" ? 1 : 0,
                });
                break;
            case "progress":
                Update(message, song => song with
                {
                    Fraction = Number(message["fraction"]) ?? song.Fraction,
                    Detail = Text(message["detail"]) ?? song.Detail,
                }, throttle: true);
                break;
            case "song":
                Update(message, song => song with
                {
                    Seconds = Number(message["seconds"]),
                    Quality = Text(message["quality"]),
                    Engine = Text(message["engine"]) ?? song.Engine,
                });
                Publish("library", new { });
                break;
            case "failed":
                Update(message, song => song with { Stage = "failed", Message = Text(message["message"]) });
                AddLog("error", $"{library.IdFor(Text(message["path"]) ?? "")}: {Text(message["message"])}");
                break;
            case "idle":
                PublishWorker();
                break;
        }
    }

    private void Started(JsonObject message)
    {
        var title = Text(message["title"]) ?? "";
        var run = Text(message["job"]) ?? "";
        var started = new List<SongState>();
        foreach (var node in message["songs"]?.AsArray() ?? [])
        {
            if (node is not JsonObject entry || Text(entry["path"]) is not { } path)
            {
                continue;
            }
            started.Add(new SongState
            {
                Id = library.IdFor(path),
                Run = run,
                Title = title,
                Index = (int)(Number(entry["index"]) ?? 1),
                Seed = (long?)Number(entry["seed"]),
                AudioPath = path,
                UpdatedAt = time.GetUtcNow(),
            });
        }

        lock (_gate)
        {
            foreach (var song in started)
            {
                _songs[song.Id] = song;
            }
            PruneFinishedLocked();
        }
        foreach (var song in started)
        {
            Publish("song", song);
        }
        PublishWorker();
    }

    private void Update(JsonObject message, Func<SongState, SongState> change, bool throttle = false)
    {
        var id = library.IdFor(Text(message["path"]) ?? "");
        SongState updated;
        var now = time.GetUtcNow();
        bool publish;
        lock (_gate)
        {
            if (!_songs.TryGetValue(id, out var song))
            {
                return;
            }
            updated = change(song) with { UpdatedAt = now };
            _songs[id] = updated;
            publish = !throttle || updated.Fraction >= 1
                || !_lastProgress.TryGetValue(id, out var last) || now - last >= ProgressInterval;
            if (publish)
            {
                _lastProgress[id] = now;
            }
        }
        if (publish)
        {
            Publish("song", updated);
        }
        if (updated.Finished)
        {
            PublishWorker();
        }
    }

    private void PruneFinishedLocked()
    {
        foreach (var old in _songs.Values.Where(s => s.Finished).OrderByDescending(s => s.UpdatedAt).Skip(FinishedCapacity).ToList())
        {
            _songs.Remove(old.Id);
            _lastProgress.Remove(old.Id);
        }
    }

    private void AddLog(string level, string text)
    {
        var entry = new LogEntry(time.GetUtcNow(), level, text);
        lock (_gate)
        {
            _log.AddLast(entry);
            if (_log.Count > LogCapacity)
            {
                _log.RemoveFirst();
            }
        }
        Publish("log", entry);
    }

    private void PublishWorker()
    {
        var studioRunning = StudioRunning();
        WorkerInfo info;
        lock (_gate)
        {
            info = WorkerInfoLocked(studioRunning);
        }
        Publish("worker", info);
    }

    private void Publish(string type, object data)
    {
        var item = new ServerEvent(type, data);
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                // A browser that stopped reading is dropped; its EventSource reconnects and starts from a fresh snapshot.
                if (!subscriber.Writer.TryWrite(item))
                {
                    subscriber.Writer.TryComplete();
                }
            }
        }
    }

    private void Unsubscribe(Channel<ServerEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }
        channel.Writer.TryComplete();
    }

    private StatusSnapshot SnapshotLocked(bool studioRunning) => new(
        WorkerInfoLocked(studioRunning),
        [.. _songs.Values.OrderBy(s => s.Run, StringComparer.Ordinal).ThenBy(s => s.Index)],
        [.. _log]);

    private WorkerInfo WorkerInfoLocked(bool studioRunning) =>
        new(_status, _songs.Values.Any(s => !s.Finished), studioRunning, _lastError, _extensions);

    /// <summary>Scanning the process table takes a moment, and worker events come in bursts: look at most every few seconds.</summary>
    private bool StudioRunning()
    {
        var now = time.GetUtcNow();
        if (now - _studioCheckedAt >= StudioCheckInterval)
        {
            _studioRunning = studio.IsRunning();
            _studioCheckedAt = now;
        }
        return _studioRunning;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken) => await ShutdownWorkerAsync();

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            await connection.DisposeAsync();
        }
    }

    public sealed class Subscription(StatusSnapshot snapshot, ChannelReader<ServerEvent> reader, Action unsubscribe) : IDisposable
    {
        public StatusSnapshot Snapshot { get; } = snapshot;

        public ChannelReader<ServerEvent> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}
