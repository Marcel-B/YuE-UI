using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Lyrics;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Queue;

/// <summary>
/// One queue for songs, renders and lyrics drafts: what cannot start right away waits here and starts on its turn,
/// instead of being refused with 409.
/// </summary>
/// <remarks>
/// The rule is the one the lyrics writer and the voice converter follow on their own: on 24 GB only one large model
/// fits, YuE2, the lyrics model or separation and Seed-VC. Songs and renders go to the worker as soon as neither a
/// draft nor a voice conversion holds the memory; the worker queues them itself and batches what it can. A lyrics
/// draft needs the worker idle as well. Jobs start strictly in order, so a draft queued behind songs waits for them
/// and a song queued behind a draft waits for that; bundling jobs by model is left for later.
/// Voice conversions keep their own queue (<see cref="VoiceConverter"/>), which waits for the memory in the same way.
/// Waiting jobs are kept in <see cref="SqliteJobStore"/> and resumed after a restart. Transcriptions are not queued
/// here: SheetSage2 runs beside YuE2 in the worker.
/// </remarks>
public sealed class JobQueue(
    SqliteJobStore store,
    WorkerHost host,
    LyricsWriter lyrics,
    VoiceConverter voices,
    SongLibrary library,
    TimeProvider time,
    ILogger<JobQueue> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// How often the head of the queue is tried again. Nothing announces that a draft or a conversion ended, and a
    /// job waits minutes anyway; a new job is tried at once.
    /// </summary>
    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Held while a job is started, so that a request and the queue's loop never start two at once.</summary>
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();
    private readonly List<(QueuedJob Job, JsonObject Payload)> _jobs = [];

    public IReadOnlyList<QueuedJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Select(j => j.Job)];
            }
        }
    }

    /// <summary>A new run: to the worker now if the memory is free and nothing waits, otherwise into the queue.</summary>
    /// <returns>The waiting job, or null when the song went to the worker.</returns>
    /// <exception cref="WorkerUnavailableException">The worker could not be started.</exception>
    public Task<QueuedJob?> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken)
    {
        var job = new QueuedJob(NewId(), JobKind.Song, request.Title?.Trim() ?? "", time.GetUtcNow(), Batch: request.Batch, Quality: request.Quality);
        return SubmitAsync(job, request.ToWorkerCommand(), cancellationToken);
    }

    /// <inheritdoc cref="GenerateAsync"/>
    public Task<QueuedJob?> RenderAsync(string run, string song, string title, string quality, string? engines, CancellationToken cancellationToken)
    {
        var job = new QueuedJob(NewId(), JobKind.Render, title, time.GetUtcNow(), SongId: $"{run}/{song}", Quality: quality);
        var payload = new JsonObject { ["run"] = run, ["song"] = song, ["quality"] = quality, ["engines"] = engines };
        return SubmitAsync(job, payload, cancellationToken);
    }

    /// <summary>A lyrics draft or revision; its result arrives as a <c>lyrics</c> event under the id returned.</summary>
    /// <returns>The draft's state: writing, or queued while it waits.</returns>
    public async Task<LyricsState> LyricsAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        var id = LyricsWriter.NewId();
        var title = request.Keywords?.Trim() ?? "";
        var job = new QueuedJob(id, JobKind.Lyrics, title.Length > 80 ? title[..80] + "…" : title, time.GetUtcNow(), Revision: request.Instruction is not null);
        var queued = await SubmitAsync(job, JsonSerializer.SerializeToNode(request, Json)!.AsObject(), cancellationToken);
        return new LyricsState { Id = id, Stage = queued is null ? "writing" : "queued", UpdatedAt = time.GetUtcNow() };
    }

    /// <returns>False when no such job waits (it may have started meanwhile).</returns>
    public bool Cancel(string id)
    {
        lock (_gate)
        {
            if (_jobs.RemoveAll(j => j.Job.Id == id) == 0)
            {
                return false;
            }
        }
        Persist(() => store.Remove(id));
        Published();
        return true;
    }

    /// <summary>Moves a waiting job by <paramref name="offset"/> places, towards the front when negative.</summary>
    /// <returns>False when no such job waits.</returns>
    public bool Move(string id, int offset)
    {
        List<string> order;
        lock (_gate)
        {
            var index = _jobs.FindIndex(j => j.Job.Id == id);
            if (index < 0)
            {
                return false;
            }
            var entry = _jobs[index];
            _jobs.RemoveAt(index);
            _jobs.Insert(Math.Clamp(index + offset, 0, _jobs.Count), entry);
            order = [.. _jobs.Select(j => j.Job.Id)];
        }
        Persist(() => store.Reorder(order));
        Published();
        // The job now at the front may be one that can start.
        _wake.Release();
        return true;
    }

    private async Task<QueuedJob?> SubmitAsync(QueuedJob job, JsonObject payload, CancellationToken cancellationToken)
    {
        await _starting.WaitAsync(cancellationToken);
        try
        {
            bool empty;
            lock (_gate)
            {
                empty = _jobs.Count == 0;
            }
            // Behind waiting jobs even if it could start: the order is the one the user sees.
            if (empty && await TryStartAsync(job, payload, cancellationToken))
            {
                return null;
            }
            lock (_gate)
            {
                _jobs.Add((job, payload));
            }
            Persist(() => store.Add(job, payload));
        }
        finally
        {
            _starting.Release();
        }
        Published();
        return job;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var stored = store.All();
            lock (_gate)
            {
                _jobs.AddRange(stored);
            }
            if (stored.Count > 0)
            {
                logger.LogInformation("Resuming {Count} queued jobs", stored.Count);
                Published();
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read the queued jobs");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartWaitingAsync(stoppingToken);
                await _wake.WaitAsync(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The queue could not start a job");
            }
        }
    }

    /// <summary>Starts jobs from the front for as long as they can start, e.g. several songs in a row.</summary>
    private async Task StartWaitingAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await _starting.WaitAsync(stoppingToken);
            try
            {
                (QueuedJob Job, JsonObject Payload) next;
                lock (_gate)
                {
                    if (_jobs.Count == 0)
                    {
                        return;
                    }
                    next = _jobs[0];
                }
                try
                {
                    if (!await TryStartAsync(next.Job, next.Payload, stoppingToken))
                    {
                        return;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Failed(next.Job, exception.Message);
                }
                lock (_gate)
                {
                    _jobs.RemoveAll(j => j.Job.Id == next.Job.Id);
                }
                Persist(() => store.Remove(next.Job.Id));
            }
            finally
            {
                _starting.Release();
            }
            Published();
        }
    }

    /// <returns>False when the memory is taken and the job has to wait.</returns>
    private async Task<bool> TryStartAsync(QueuedJob job, JsonObject payload, CancellationToken cancellationToken)
    {
        if (job.Kind == JobKind.Lyrics)
        {
            return StartLyrics(job, payload);
        }

        JsonObject? command = null;
        string? directory = null;
        if (job.Kind == JobKind.Song)
        {
            command = payload;
        }
        else if (library.SongDirectory(Text(payload["run"]), Text(payload["song"])) is not { } found)
        {
            throw new InvalidOperationException("The song is gone.");
        }
        else
        {
            directory = found;
        }

        // Announced before looking: the lyrics writer and the voice converter claim the memory first and then look at
        // the worker, so one of the two always sees the other.
        host.ExpectSongs();
        if (lyrics.IsWriting || voices.IsConverting)
        {
            host.ExpectNoSongs();
            return false;
        }
        try
        {
            if (command is not null)
            {
                await host.GenerateAsync(command, cancellationToken);
            }
            else
            {
                await host.RenderAsync(directory!, Text(payload["quality"]), payload["engines"]?.GetValue<string>(), cancellationToken);
            }
        }
        catch
        {
            host.ExpectNoSongs();
            throw;
        }
        return true;
    }

    private bool StartLyrics(QueuedJob job, JsonObject payload)
    {
        var request = payload.Deserialize<LyricsRequest>(Json) ?? throw new JsonException("The lyrics request is unreadable.");
        var revision = request.Lyrics is not null && request.Instruction is not null ? new LyricsRevision(request.Lyrics, request.Instruction) : null;
        try
        {
            lyrics.Start(
                request.Keywords,
                request.Style,
                request.Language ?? LyricsLanguage.English,
                request.Model,
                request.Image,
                revision,
                job.Id,
                () => voices.IsConverting);
            return true;
        }
        catch (LyricsBusyException)
        {
            return false;
        }
    }

    /// <summary>A job that could not start leaves the queue; the log says why, and a draft's form hears it too.</summary>
    private void Failed(QueuedJob job, string message)
    {
        logger.LogWarning("The queued {Kind} {Id} could not start: {Message}", job.Kind, job.Id, message);
        if (job.Kind == JobKind.Lyrics)
        {
            host.UpdateLyrics(new LyricsState { Id = job.Id, Stage = "failed", Message = message, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            host.LogError($"{(job.Title.Length > 0 ? job.Title : job.SongId ?? job.Kind.ToString())}: {message}");
        }
    }

    private void Published() => host.UpdateQueue(Jobs);

    /// <summary>The queue works from memory; a database that cannot be written only costs the resumption after a restart.</summary>
    private void Persist(Action write)
    {
        try
        {
            write();
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not store the queue");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
}
