namespace YueUI.Api.Queue;

/// <summary>What waits in <see cref="JobQueue"/>.</summary>
public enum JobKind
{
    /// <summary>A new run from the form (<see cref="GenerateRequest"/>).</summary>
    Song,

    /// <summary>A song synthesized again from its tokens, e.g. a draft at full quality.</summary>
    Render,

    /// <summary>A lyrics draft or revision; its id is the <see cref="Worker.LyricsState"/>'s the form waits for.</summary>
    Lyrics,
}

/// <summary>A job waiting for its turn, as the queue on the create page shows it.</summary>
/// <param name="Title">The run's title (may be empty: the worker names it), the song's for a render, the keywords for lyrics.</param>
/// <param name="SongId">The song a render is for.</param>
/// <param name="Batch">How many songs a new run makes.</param>
/// <param name="Quality">"draft" or "full", for songs and renders.</param>
/// <param name="Revision">A lyrics job that revises the lyrics rather than drafting new ones.</param>
public sealed record QueuedJob(
    string Id,
    JobKind Kind,
    string Title,
    DateTimeOffset CreatedAt,
    string? SongId = null,
    int? Batch = null,
    string? Quality = null,
    bool Revision = false);
