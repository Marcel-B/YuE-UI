namespace YueUI.Api.Queue;

/// <summary>How <see cref="JobQueue"/> bundles jobs by model, configured under <c>Queue</c>.</summary>
public sealed class QueueOptions
{
    public const string Section = "Queue";

    /// <summary>
    /// How long a lyrics draft or a voice version may wait while songs for YuE2 go ahead of it. Loading YuE2 takes
    /// minutes and the worker batches songs it gets together, so songs that arrive while YuE2 holds the memory go
    /// straight to it; after this long, what waited goes first and new songs wait for it. Zero keeps strict order.
    /// </summary>
    public TimeSpan BundleWindow { get; set; } = TimeSpan.FromMinutes(20);
}
