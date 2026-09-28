using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Lyrics;
using YueUI.Api.Voices;
using YueUI.Api.Worker;

namespace YueUI.Api.Speech;

/// <summary>
/// Whether a speech model holds the memory. A class of its own rather than a property of <see cref="SpeechLab"/>:
/// the voice converter has to see it, and the lab has to see the converter.
/// </summary>
public sealed class SpeechActivity
{
    private volatile bool _speaking;

    /// <summary>YuE2, the lyrics model and voice conversions wait while it is set.</summary>
    public bool IsSpeaking
    {
        get => _speaking;
        internal set => _speaking = value;
    }
}

/// <summary>
/// The speech lab's takes, spoken one at a time: a text, a model and optionally a recorded voice to clone
/// (<see cref="ISpeechEngine"/>). For hearing which local model could speak a podcast before building one.
/// </summary>
/// <remarks>
/// A speech model is 3 to 17 GB, so it follows the rule the lyrics writer and the voice converter follow on 24 GB:
/// a take waits while songs are generated, lyrics written or a song sung with another voice, an idle YuE worker is
/// shut down first, and while <see cref="SpeechActivity.IsSpeaking"/> the others wait. The flag is taken before
/// looking, as they do, so two sides never both see the memory free. Takes are not in <c>JobQueue</c>: a take is short,
/// and the lab lists its own. Queued takes survive a restart; one that was halfway is marked failed.
/// </remarks>
public sealed class SpeechLab(
    SqliteSpeechStore store,
    ISpeechEngine engine,
    SpeechActivity activity,
    WorkerHost host,
    LyricsWriter lyrics,
    VoiceConverter voices,
    IOptions<SpeechOptions> options,
    TimeProvider time,
    ILogger<SpeechLab> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private string? _running;
    private CancellationTokenSource? _cancel;

    /// <summary>One take per model, spoken in the order given.</summary>
    public IReadOnlyList<SpeechTake> Enqueue(string text, SpeechVoice? voice, IEnumerable<SpeechModel> models)
    {
        var takes = new List<SpeechTake>();
        foreach (var model in models)
        {
            var now = time.GetUtcNow();
            var take = new SpeechTake
            {
                Id = Guid.NewGuid().ToString("N"),
                ModelId = model.Id,
                ModelLabel = model.Label,
                Text = text,
                VoiceId = voice?.Id,
                VoiceLabel = voice?.Label,
                CreatedAt = now,
                UpdatedAt = now,
            };
            store.AddTake(take);
            host.UpdateSpeech(take);
            _queue.Writer.TryWrite(take.Id);
            takes.Add(take);
        }
        return takes;
    }

    /// <summary>Stops the take if it is being spoken, and removes it with its audio.</summary>
    public void Delete(SpeechTake take)
    {
        lock (_gate)
        {
            if (_running == take.Id)
            {
                _cancel?.Cancel();
            }
        }
        store.RemoveTake(take.Id);
        host.UpdateSpeech(take with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            foreach (var take in store.Unfinished())
            {
                if (take.Stage == "queued")
                {
                    // Into the snapshot as well, so the queue shows it before its turn comes.
                    host.UpdateSpeech(take);
                    _queue.Writer.TryWrite(take.Id);
                }
                else
                {
                    Update(take, t => t with { Stage = "failed", Message = "The server restarted while the take was spoken." });
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the queued takes");
        }

        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(string id, CancellationToken stoppingToken)
    {
        if (store.Take(id) is not { Finished: false } take)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-speech-{id}");
        try
        {
            var model = options.Value.ResolvedModels.FirstOrDefault(m => m.Id == take.ModelId)
                ?? throw new SpeechException($"The model {take.ModelId} is no longer offered.");
            var voice = take.VoiceId is { } voiceId ? store.Voice(voiceId) ?? throw new SpeechException("The recorded voice is gone.") : null;

            await WaitForMemoryAsync(cancel.Token);
            // YuE2 keeps its model for ten idle minutes; the speech model needs the memory now.
            await host.ShutdownWorkerAsync();

            take = Update(take, t => t with { Stage = "loading" });
            Directory.CreateDirectory(work);
            var output = Path.Combine(work, "take.wav");
            var job = new SpeechJob(model, take.Text, voice is null ? null : store.VoicePath(voice.Id), voice?.Transcript is { Length: > 0 } words ? words : null, output);
            var result = await engine.SpeakAsync(job, stage => take = Update(take, t => t with { Stage = stage is "loading" ? "loading" : "speaking" }), cancel.Token);

            var target = store.TakePath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(output, target, overwrite: true);
            if (store.Take(id) is null)
            {
                // Deleted while it was spoken.
                File.Delete(target);
            }
            else
            {
                Update(take, t => t with
                {
                    Stage = "done",
                    Seconds = WavInfo.Seconds(target),
                    LoadSeconds = result.LoadSeconds,
                    SpeakSeconds = result.SpeakSeconds,
                    PeakMemoryGb = result.PeakMemoryGb,
                });
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                Update(take, t => t with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The take {Id} with {Model} failed", id, take.ModelId);
            Update(take, t => t with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            activity.IsSpeaking = false;
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not delete {Directory}", work);
            }
        }
    }

    /// <summary>Checked again once the flag is set, so a song or a version started in between is not overlooked.</summary>
    private async Task WaitForMemoryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!Occupied())
            {
                activity.IsSpeaking = true;
                if (!Occupied())
                {
                    return;
                }
                activity.IsSpeaking = false;
            }
            await Task.Delay(options.Value.WaitInterval, time, cancellationToken);
        }

        bool Occupied() => host.Snapshot().Worker.Busy || lyrics.IsWriting || voices.IsConverting;
    }

    /// <summary>Stores the change (a take deleted meanwhile stays deleted) and tells the browsers.</summary>
    private SpeechTake Update(SpeechTake take, Func<SpeechTake, SpeechTake> change)
    {
        var updated = change(take) with { UpdatedAt = time.GetUtcNow() };
        try
        {
            if (!store.UpdateTake(updated))
            {
                return updated;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the take {Id}", take.Id);
        }
        host.UpdateSpeech(updated);
        return updated;
    }
}
