namespace YueUI.Api.Video;

/// <summary>
/// A song as a video to upload: its cover (or none) over a blurred copy of itself, optionally the title, an analyzer
/// or a waveform, and drifting particles or a plasma ball moving to the music. The browser draws the still layers (it already draws covers),
/// ffmpeg animates and encodes them (<see cref="FfmpegVideoRenderer"/>). The files are this app's own, in
/// <c>videos/&lt;id&gt;/</c> next to its database. <see cref="Stage"/>: queued, rendering, done, failed or cancelled.
/// </summary>
public sealed record VideoState
{
    public required string Id { get; init; }

    /// <summary><c>run/songN</c>.</summary>
    public required string SongId { get; init; }

    /// <summary>The run's title when it was asked for, for the queue, the notification and the file name.</summary>
    public required string Title { get; init; }

    /// <inheritdoc cref="VideoFormats"/>
    public required string Format { get; init; }

    /// <inheritdoc cref="VideoEffects"/>
    public required string Effect { get; init; }

    /// <inheritdoc cref="VideoMotions"/>
    public string Motion { get; init; } = VideoMotions.None;

    /// <summary>The cover sits in the middle; without it only its blurred copy in the background is left.</summary>
    public bool ShowCover { get; init; } = true;

    /// <summary>The title fades in under the cover.</summary>
    public bool ShowTitle { get; init; }

    public string Stage { get; init; } = "queued";

    /// <summary>How much of the song is rendered, 0 to 1.</summary>
    public double Fraction { get; init; }

    /// <summary>Why it failed.</summary>
    public string? Message { get; init; }

    /// <summary>The finished file's size.</summary>
    public long? Bytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Finished => Stage is "done" or "failed" or "cancelled";
}

/// <summary>
/// <c>landscape</c>: 1920×1080, what YouTube shows in its player. <c>portrait</c>: 1080×1920, for Shorts, Reels and
/// TikTok, which crop anything else.
/// </summary>
public static class VideoFormats
{
    public const string Landscape = "landscape";
    public const string Portrait = "portrait";

    public static readonly IReadOnlyList<string> All = [Landscape, Portrait];
}

/// <summary><c>bars</c>: a spectrum analyzer; <c>wave</c>: the waveform; <c>none</c>: nothing in the band.</summary>
public static class VideoEffects
{
    public const string Bars = "bars";
    public const string Wave = "wave";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Bars, Wave, None];
}

/// <summary>
/// What moves behind the cover. <c>particles</c>: specks drifting up that light up with the bass; <c>plasma</c>: a
/// plasma ball, lightning from the middle (behind the cover, if there is one) that flickers and flares with the bass;
/// <c>none</c>: nothing.
/// </summary>
public static class VideoMotions
{
    public const string None = "none";
    public const string Particles = "particles";
    public const string Plasma = "plasma";

    public static readonly IReadOnlyList<string> All = [None, Particles, Plasma];
}

/// <summary>
/// Where things go in a frame. The browser draws cover and title around the band (<c>video.ts</c> holds the same
/// numbers), the analyzer or waveform fills the band; particles and background take the whole frame.
/// </summary>
public sealed record VideoLayout(int Width, int Height, int BandY, int BandHeight)
{
    /// <summary>YouTube and the phones' apps play 30 frames a second; more only makes the file bigger.</summary>
    public const int FrameRate = 30;

    /// <summary>One analyzer bar and the gap after it, in pixels.</summary>
    public const int BarPitch = 20;

    public const int BarWidth = 14;

    /// <summary>
    /// The plasma ball comes as this many pictures of its square stacked on top of each other (<see cref="PlasmaSize"/>
    /// wide, this many times as high); the renderer flickers between them.
    /// </summary>
    public const int PlasmaFrames = 8;

    /// <summary>
    /// The plasma ball's square, centred on the cover's middle (<c>video.ts</c> draws the cover there), large enough
    /// for the lightning to reach well beyond a cover.
    /// </summary>
    public int PlasmaSize => Width == 1080 ? 1080 : 1000;

    public int PlasmaX => (Width - PlasmaSize) / 2;

    /// <remarks>The landscape square reaches above the frame; overlay cuts it off, the glass stays inside.</remarks>
    public int PlasmaY => Width == 1080 ? 70 : -130;

    /// <remarks>
    /// The vertical band sits above the bottom fifth, which Shorts, Reels and TikTok cover with caption and buttons.
    /// </remarks>
    public static VideoLayout For(string format) => format == VideoFormats.Portrait
        ? new VideoLayout(1080, 1920, 1270, 220)
        : new VideoLayout(1920, 1080, 850, 180);
}

/// <summary>
/// The still layers the browser drew, as PNGs of the frame's size (the plasma ball's frames excepted, see
/// <see cref="VideoLayout.PlasmaFrames"/>); all but the background only when asked for.
/// </summary>
public sealed record VideoLayers(string Background, string? Cover, string? Title, string? Motion);

/// <summary>Makes the video from a song's FLAC and the layers. Tests replace it.</summary>
public interface IVideoRenderer
{
    /// <summary>False when this machine has no ffmpeg.</summary>
    bool Available { get; }

    /// <param name="seconds">The song's length from its <c>result.json</c>, for when ffprobe cannot tell.</param>
    /// <param name="progress">How much is rendered, 0 to 1.</param>
    /// <exception cref="VideoException">ffmpeg failed; the message says why.</exception>
    Task RenderAsync(
        VideoState video,
        string flac,
        double? seconds,
        VideoLayers layers,
        string output,
        Action<double> progress,
        CancellationToken cancellationToken);
}

public sealed class VideoException(string message) : Exception(message);
