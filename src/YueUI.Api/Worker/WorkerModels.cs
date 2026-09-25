using System.Text.Json.Serialization;

namespace YueUI.Api.Worker;

public enum WorkerStatus
{
    /// <summary>No worker process; the next command starts one.</summary>
    Stopped,

    /// <summary>The process runs but has not said <c>ready</c> yet (Python imports take a few seconds).</summary>
    Starting,

    Ready,
}

/// <summary>
/// A song on its way through the worker, keyed by <see cref="Id"/> (<c>run/songN</c>, as in the library).
/// <see cref="Stage"/> is the worker's: queued, planning, tokens, synth, decode, ready, failed or cancelled.
/// </summary>
public sealed record SongState
{
    public required string Id { get; init; }

    public required string Run { get; init; }

    public string Title { get; init; } = "";

    public int Index { get; init; }

    public long? Seed { get; init; }

    public string Stage { get; init; } = "queued";

    public string Detail { get; init; } = "";

    /// <summary>Progress of the current stage, 0–1.</summary>
    public double Fraction { get; init; }

    /// <summary>Where the stage runs, e.g. "gpu" or "ane", when the worker says.</summary>
    public string? Engine { get; init; }

    /// <summary>Length of the finished audio.</summary>
    public double? Seconds { get; init; }

    public string? Quality { get; init; }

    /// <summary>Why the song failed.</summary>
    public string? Message { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// The stages this song went through, each with the time it began, oldest first; the last one is <see cref="Stage"/>.
    /// The worker only says when a stage begins, so this is where the interface gets how long each one took.
    /// </summary>
    public IReadOnlyList<StageTime> Stages { get; init; } = [];

    /// <summary>A render from saved tokens: it has no planning or composing ahead of it.</summary>
    public bool Render { get; init; }

    public bool Finished => Stage is "ready" or "failed" or "cancelled";

    /// <summary>Moves on to <paramref name="stage"/>; the worker repeats a stage's event when only its detail changes.</summary>
    public SongState Entering(string stage, DateTimeOffset at) =>
        stage == Stage ? this : this with { Stage = stage, Stages = [.. Stages, new StageTime(stage, at)] };

    /// <summary>The worker addresses songs by this path (cancel).</summary>
    [JsonIgnore]
    public string AudioPath { get; init; } = "";
}

public sealed record StageTime(string Stage, DateTimeOffset StartedAt);

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);

/// <param name="Status">Whether a worker process runs.</param>
/// <param name="Busy">Whether any song is queued or running.</param>
/// <param name="StudioRunning">
/// The YuE Studio app is open. Its own worker then holds a second copy of the model, and two generating workers
/// can run the machine out of memory, so the interface warns.
/// </param>
/// <param name="LastError">The worker's last error event, until it starts again.</param>
/// <param name="Extensions">
/// Whether the last worker that said ready took YueUI's extra generate fields (sampling, full-quality steps, songs
/// over six minutes); null until a worker has started. False when a YuE Studio update changed what the extension
/// patches: the worker then runs as shipped and ignores those fields.
/// </param>
public sealed record WorkerInfo(WorkerStatus Status, bool Busy, bool StudioRunning, string? LastError, bool? Extensions = null);

/// <summary>Everything a client needs to draw the queue; also the first event of every event stream.</summary>
public sealed record StatusSnapshot(
    WorkerInfo Worker,
    IReadOnlyList<SongState> Songs,
    IReadOnlyList<LogEntry> Log,
    IReadOnlyList<TranscriptionState>? Transcriptions = null,
    LyricsState? Lyrics = null);

/// <summary>
/// The last lyrics draft (Lyrics/LyricsWriter.cs). It arrives as an event rather than as the answer to the request:
/// loading the model and writing take long enough for a phone to lock or a proxy to give up on the connection.
/// </summary>
public sealed record LyricsState
{
    public required string Id { get; init; }

    /// <summary>writing, done or failed.</summary>
    public string Stage { get; init; } = "writing";

    public string? Lyrics { get; init; }

    /// <summary>Why it failed, e.g. LM Studio's own message.</summary>
    public string? Message { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed";
}

/// <summary>
/// A transcription in flight: a recording going through SheetSage2 in the worker, keyed by <see cref="Id"/> (this
/// server's; the worker echoes it). <see cref="Stage"/> is the worker's: starting, progress, done, failed or cancelled.
/// </summary>
public sealed record TranscriptionState
{
    public required string Id { get; init; }

    /// <summary>The recording's file name as uploaded.</summary>
    public required string FileName { get; init; }

    /// <summary>"melody-full" or "melody-vocal".</summary>
    public required string Task { get; init; }

    public string Stage { get; init; } = "starting";

    /// <summary>0–1 when SheetSage2 reports it; null while it only says that it is still busy.</summary>
    public double? Fraction { get; init; }

    public string Detail { get; init; } = "";

    /// <summary>The melody score, once done.</summary>
    public string? Abc { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The folder in the transcription library, once done.</summary>
    public string? Result { get; init; }

    /// <summary>Why it failed, and the worker's code for it (busy, no_env, afconvert, abc_error, crash).</summary>
    public string? Message { get; init; }

    public string? Code { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";

    /// <summary>The uploaded copy the worker reads; deleted once the transcription has finished.</summary>
    [JsonIgnore]
    public string UploadDirectory { get; init; } = "";
}
