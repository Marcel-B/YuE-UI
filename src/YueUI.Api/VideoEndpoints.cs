using Microsoft.AspNetCore.Mvc;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Video;

namespace YueUI.Api;

/// <summary>
/// Music videos of a song (<see cref="VideoMaker"/>): the browser sends the still layers it drew as PNGs of the
/// frame's size, the server animates and encodes them in the background and keeps the MP4 to download or share.
/// </summary>
public static class VideoEndpoints
{
    /// <summary>A full-frame PNG of a photo is a few megabytes; this only guards against anything else.</summary>
    public const long MaxLayerBytes = 20 * 1024 * 1024;

    public static RouteGroupBuilder MapVideoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/songs/{run}/{song}/videos", (string run, string song, SongLibrary library, SqliteVideoStore store) =>
            library.SongDirectory(run, song) is null ? Results.NotFound() : Results.Ok(store.ForSong($"{run}/{song}")));
        api.MapPost("/songs/{run}/{song}/videos", AddAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: 4 * MaxLayerBytes)
            .WithMetadata(new RequestSizeLimitAttribute(4 * MaxLayerBytes + 64 * 1024));
        // Range requests: Safari plays a video only from a source that answers them.
        api.MapGet("/videos/{id}/file", (string id, bool? download, SqliteVideoStore store) =>
        {
            if (store.Get(id) is not { Stage: "done" } video || !File.Exists(store.VideoPath(id)))
            {
                return Results.NotFound();
            }
            return Results.File(store.VideoPath(id), "video/mp4", download == true ? FileName(video) : null, enableRangeProcessing: true);
        });
        api.MapDelete("/videos/{id}", (string id, SqliteVideoStore store, VideoMaker maker) =>
        {
            if (store.Get(id) is not { } video)
            {
                return Results.NotFound();
            }
            maker.Delete(video);
            return Results.NoContent();
        });
        return api;
    }

    /// <summary>"Neon Night-song2-youtube.mp4": the title, the song and where it is meant to go.</summary>
    public static string FileName(VideoState video) =>
        $"{LibraryEndpoints.FileName(video.Title, video.SongId.Split('/')[0])}-{video.SongId.Split('/')[^1]}-{(video.Format == VideoFormats.Portrait ? "vertical" : "youtube")}.mp4";

    private static async Task<IResult> AddAsync(
        string run,
        string song,
        [FromForm] string? format,
        [FromForm] string? effect,
        [FromForm] string? motion,
        [FromForm] bool? showCover,
        [FromForm] bool? showTitle,
        IFormFileCollection files,
        SongLibrary library,
        SqliteVideoStore store,
        VideoMaker maker,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        if (!maker.Available)
        {
            return Results.Problem(title: "Videos need ffmpeg, which is not installed.", statusCode: StatusCodes.Status501NotImplemented);
        }
        var errors = new Dictionary<string, string[]>();
        if (!VideoFormats.All.Contains(format ?? ""))
        {
            errors["format"] = [$"One of {string.Join(", ", VideoFormats.All)}."];
        }
        if (!VideoEffects.All.Contains(effect ?? ""))
        {
            errors["effect"] = [$"One of {string.Join(", ", VideoEffects.All)}."];
        }
        motion ??= VideoMotions.None;
        if (!VideoMotions.All.Contains(motion))
        {
            errors["motion"] = [$"One of {string.Join(", ", VideoMotions.All)}."];
        }
        // Older pages sent no showCover; they always drew the cover.
        var withCover = showCover != false;
        var layout = VideoLayout.For(format ?? "");
        // The layers by their form name; each must be a PNG of exactly the size the renderer lays it out at, or
        // nothing lines up.
        var wanted = new List<(string Name, int Width, int Height)> { ("background", layout.Width, layout.Height) };
        if (withCover)
        {
            wanted.Add(("cover", layout.Width, layout.Height));
        }
        if (showTitle == true)
        {
            wanted.Add(("title", layout.Width, layout.Height));
        }
        if (motion == VideoMotions.Particles)
        {
            wanted.Add((VideoMotions.Particles, layout.Width, layout.Height));
        }
        else if (motion == VideoMotions.Plasma)
        {
            wanted.Add((VideoMotions.Plasma, layout.PlasmaSize, layout.PlasmaSize * VideoLayout.PlasmaFrames));
        }
        var layers = new Dictionary<string, byte[]>();
        foreach (var (name, wantedWidth, wantedHeight) in wanted)
        {
            var file = files.GetFile(name);
            var data = file is { Length: > 0 and <= MaxLayerBytes } ? await ReadAsync(file, cancellationToken) : null;
            if (data is null || PngSize(data) is not var (width, height) || width != wantedWidth || height != wantedHeight)
            {
                errors[name] = [$"A PNG of {wantedWidth}×{wantedHeight}."];
                continue;
            }
            layers[name] = data;
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var now = time.GetUtcNow();
        var video = new VideoState
        {
            Id = Guid.NewGuid().ToString("N"),
            SongId = $"{run}/{song}",
            Title = library.TitleOf(run, directory),
            Format = format!,
            Effect = effect!,
            Motion = motion,
            ShowCover = withCover,
            ShowTitle = showTitle == true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Directory.CreateDirectory(store.Folder(video.Id));
        foreach (var (name, data) in layers)
        {
            await File.WriteAllBytesAsync(Path.Combine(store.Folder(video.Id), $"{name}.png"), data, cancellationToken);
        }
        return Results.Accepted($"/api/videos/{video.Id}/file", maker.Enqueue(video));
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>Width and height from the PNG's header chunk, which always comes first; null for anything else.</summary>
    public static (int Width, int Height)? PngSize(byte[] data) =>
        data is [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, _, _, _, _, (byte)'I', (byte)'H', (byte)'D', (byte)'R', ..] && data.Length >= 24
            ? (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)))
            : null;
}
