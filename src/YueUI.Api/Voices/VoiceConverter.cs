using System.Threading.Channels;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Lyrics;
using YueUI.Api.Worker;

namespace YueUI.Api.Voices;

/// <summary>
/// Sings songs with another voice, one at a time: StemMyWav separates the vocals, ChangeMyVoice (Seed-VC) converts
/// them to a reference voice, ffmpeg mixes them back under the instrumental. The result is a version of the song
/// (<see cref="SqliteVersionStore"/>); the song itself is not touched.
/// </summary>
/// <remarks>
/// YuE2 does not know voices: no reference audio, no speaker embedding, and the same seed does not give the same
/// singer in another song. Converting afterwards is what makes a voice recognizable across songs.
/// The Mac has 24 GB, and YuE2, a separation model and Seed-VC do not fit beside each other, so this works like the
/// lyrics writer: a version waits while songs are generated or lyrics written, an idle YuE worker is shut down
/// first, and while <see cref="IsConverting"/> no song or draft starts. Queued versions survive a restart; one that
/// was halfway is marked failed, since its intermediate files are gone.
/// </remarks>
public sealed class VoiceConverter(
    SqliteVersionStore store,
    SongLibrary library,
    WorkerHost host,
    LyricsWriter lyrics,
    VoiceClient voices,
    StemClient stems,
    IAudioMixer mixer,
    IOptions<VoiceOptions> options,
    TimeProvider time,
    ILogger<VoiceConverter> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private string? _running;
    private CancellationTokenSource? _cancel;
    private volatile bool _converting;

    /// <summary>A separation or conversion holds the memory; YuE2 and the lyrics model have to wait.</summary>
    public bool IsConverting => _converting;

    /// <summary>
    /// When the version next in line was asked for, while it waits for the memory; null when none waits. The queue
    /// stops sending songs ahead of it once it has waited too long (<see cref="Queue.QueueOptions.BundleWindow"/>).
    /// </summary>
    public DateTimeOffset? WaitingSince => Interlocked.Read(ref _waitingTicks) is > 0 and var ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>UTC ticks behind <see cref="WaitingSince"/>, 0 for none; read by requests while the queue loop writes.</summary>
    private long _waitingTicks;

    /// <summary>Whether a version of a song of the run (or of that song) is queued or in the works.</summary>
    public bool IsWorkingOn(string run, string? song = null) =>
        store.Unfinished().Any(v => song is null ? v.Run == run : v.SongId == $"{run}/{song}");

    public VersionState Enqueue(string songId, string title, ReferenceVoice voice, VersionRequest request)
    {
        var now = time.GetUtcNow();
        var version = new VersionState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = songId,
            Title = title,
            VoiceId = voice.Id,
            VoiceLabel = voice.Label,
            SemiToneShift = request.SemiToneShift,
            Strength = request.Strength,
            DiffusionSteps = request.DiffusionSteps,
            KeepReverb = request.KeepReverb,
            StemModel = options.Value.StemModel,
            CreatedAt = now,
            UpdatedAt = now,
        };
        store.Add(version);
        host.UpdateVersion(version);
        // The library lists the version from now on, in every open browser.
        host.LibraryChanged();
        _queue.Writer.TryWrite(version.Id);
        return version;
    }

    /// <summary>Stops the version if it runs, and removes it with its audio.</summary>
    public void Delete(VersionState version)
    {
        lock (_gate)
        {
            if (_running == version.Id)
            {
                _cancel?.Cancel();
            }
        }
        store.Remove(version.Id);
        host.UpdateVersion(version with { Stage = "cancelled", UpdatedAt = time.GetUtcNow() });
        host.LibraryChanged();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            foreach (var version in store.Unfinished())
            {
                if (version.Stage == "queued")
                {
                    _queue.Writer.TryWrite(version.Id);
                }
                else
                {
                    Update(version, v => v with { Stage = "failed", Message = "The server restarted while the version was being made." });
                }
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the queued versions");
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
        if (store.Get(id) is not { Finished: false } version)
        {
            return;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _running = id;
            _cancel = cancel;
        }
        var work = Path.Combine(Path.GetTempPath(), $"yueui-voice-{id}");
        string? job = null;
        try
        {
            Interlocked.Exchange(ref _waitingTicks, version.CreatedAt.UtcTicks);
            try
            {
                await WaitForMemoryAsync(cancel.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _waitingTicks, 0);
            }
            if (library.SongDirectory(version.Run, version.SongId[(version.Run.Length + 1)..]) is not { } directory
                || !File.Exists(Path.Combine(directory, "audio.flac")))
            {
                throw new VoiceServiceException("The song is gone.");
            }
            // YuE2 keeps its model for ten idle minutes; the separation needs the memory now.
            await host.ShutdownWorkerAsync();

            version = Update(version, v => v with { Stage = "separating" });
            var separated = await stems.SeparateAsync(Path.Combine(directory, "audio.flac"), version.StemModel, work, cancel.Token);

            version = Update(version, v => v with { Stage = "converting", Fraction = 0 });
            var started = await voices.StartJobAsync(version, separated.Vocals, cancel.Token);
            job = started.Id;
            await WaitForJobAsync(version, started, cancel.Token);
            var converted = Path.Combine(work, "converted.wav");
            await voices.DownloadResultAsync(job, converted, cancel.Token);

            version = Update(version, v => v with { Stage = "mixing", Fraction = 0 });
            var mix = Path.Combine(work, "mix.flac");
            await mixer.MixAsync(new MixInput(separated.Instrumental, converted, separated.Vocals, version.KeepReverb ? separated.Reverb : null), mix, cancel.Token);
            var target = store.FilePath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(mix, target, overwrite: true);

            if (store.Get(id) is null)
            {
                // Deleted while it was mixed.
                File.Delete(target);
            }
            else
            {
                Update(version, v => v with { Stage = "done", Fraction = 1 });
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                Update(version, v => v with { Stage = "cancelled" });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The version {Id} of {Song} failed", id, version.SongId);
            Update(version, v => v with { Stage = "failed", Message = exception.Message });
        }
        finally
        {
            _converting = false;
            lock (_gate)
            {
                _running = null;
                _cancel = null;
            }
            if (job is not null)
            {
                await voices.DeleteJobAsync(job);
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

    /// <summary>Checked again once <see cref="IsConverting"/> is set, so a song sent in between is not overlooked.</summary>
    private async Task WaitForMemoryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!Occupied())
            {
                _converting = true;
                if (!Occupied())
                {
                    return;
                }
                _converting = false;
            }
            await Task.Delay(options.Value.WaitInterval, time, cancellationToken);
        }

        bool Occupied() => host.Snapshot().Worker.Busy || lyrics.IsWriting;
    }

    /// <summary>ChangeMyVoice says how long it expects; the fraction is the time since the start against that.</summary>
    private async Task WaitForJobAsync(VersionState version, VoiceJob job, CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (job.Status)
            {
                case "COMPLETED":
                    return;
                case "FAILED" or "CANCELLED":
                    throw new VoiceServiceException(job.Error ?? $"The voice service reports the job as {job.Status.ToLowerInvariant()}.");
            }
            if (job.StartedAt is { } started && job.EstimatedSeconds > 0)
            {
                var fraction = Math.Min(0.99, (time.GetUtcNow() - started).TotalSeconds / job.EstimatedSeconds);
                Update(version, v => v with { Fraction = Math.Round(fraction, 3) });
            }
            await Task.Delay(options.Value.PollInterval, time, cancellationToken);
            job = await voices.GetJobAsync(job.Id, cancellationToken);
        }
    }

    /// <summary>Stores the change (a version deleted meanwhile stays deleted) and tells the browsers.</summary>
    private VersionState Update(VersionState version, Func<VersionState, VersionState> change)
    {
        var updated = change(version) with { UpdatedAt = time.GetUtcNow() };
        try
        {
            if (!store.Update(updated))
            {
                return updated;
            }
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(exception, "Could not store the version {Id}", version.Id);
        }
        host.UpdateVersion(updated);
        if (updated.Finished)
        {
            host.LibraryChanged();
        }
        return updated;
    }
}
