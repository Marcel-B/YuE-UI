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

    public bool Finished => Stage is "ready" or "failed" or "cancelled";

    /// <summary>The worker addresses songs by this path (cancel).</summary>
    [JsonIgnore]
    public string AudioPath { get; init; } = "";
}

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);

/// <param name="Status">Whether a worker process runs.</param>
/// <param name="Busy">Whether any song is queued or running.</param>
/// <param name="StudioRunning">
/// The YuE Studio app is open. Its own worker then holds a second copy of the model, and two generating workers
/// can run the machine out of memory, so the interface warns.
/// </param>
/// <param name="LastError">The worker's last error event, until it starts again.</param>
public sealed record WorkerInfo(WorkerStatus Status, bool Busy, bool StudioRunning, string? LastError);

/// <summary>Everything a client needs to draw the queue; also the first event of every event stream.</summary>
public sealed record StatusSnapshot(WorkerInfo Worker, IReadOnlyList<SongState> Songs, IReadOnlyList<LogEntry> Log);
