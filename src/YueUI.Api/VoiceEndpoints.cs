using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>
/// Reference voices (kept by ChangeMyVoice, managed here like in yue-to-logic-pro) and songs sung with them
/// (<see cref="VoiceConverter"/>). The key stays in this server's configuration; the browser only learns ids.
/// </summary>
public static class VoiceEndpoints
{
    /// <summary>A reference voice is a few seconds of singing; ChangeMyVoice keeps only 25 anyway (the first, or from the chosen start).</summary>
    public const long MaxVoiceBytes = 64L * 1024 * 1024;

    public static RouteGroupBuilder MapVoiceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/voice", (IOptions<VoiceOptions> options) =>
            new VoiceInfo(options.Value.VoicesConfigured, options.Value.ConversionConfigured, options.Value.StemsConfigured));
        api.MapGet("/voices", (VoiceClient voices, CancellationToken cancellationToken) =>
            Call(async () => Results.Ok(await voices.ListVoicesAsync(cancellationToken))));
        api.MapPost("/voices", AddVoiceAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxVoiceBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxVoiceBytes);
        api.MapGet("/voices/{id}/audio", VoiceAudioAsync);
        api.MapDelete("/voices/{id}", (string id, VoiceClient voices, CancellationToken cancellationToken) =>
            Call(async () =>
            {
                await voices.DeleteVoiceAsync(id, cancellationToken);
                return Results.NoContent();
            }));

        api.MapPost("/songs/{run}/{song}/versions", AddVersionAsync);
        // Range requests, as for the song itself: the player seeks and starts before the whole FLAC is there.
        api.MapGet("/songs/{run}/{song}/versions/{id}/audio", (string run, string song, string id, bool? download, SqliteVersionStore store, SongLibrary library) =>
            VersionAudio(run, song, id, download == true, store, library));
        api.MapDelete("/songs/{run}/{song}/versions/{id}", (string run, string song, string id, SqliteVersionStore store, VoiceConverter converter) =>
        {
            if (store.Get(id) is not { } version || version.SongId != $"{run}/{song}")
            {
                return Results.NotFound();
            }
            converter.Delete(version);
            return Results.NoContent();
        });
        return api;
    }

    private static async Task<IResult> AddVoiceAsync(
        [FromForm] string? label,
        IFormFile? file,
        [FromForm] double? startSeconds,
        [FromForm] double? endSeconds,
        VoiceClient voices,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 100)
        {
            errors["label"] = ["A name for the voice, up to 100 characters."];
        }
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording of the voice: WAV, MP3, FLAC, M4A or OGG."];
        }
        // Checked here too so the form hears it in its own words; ChangeMyVoice refuses the same.
        if (startSeconds is { } start && (!double.IsFinite(start) || start < 0))
        {
            errors["startSeconds"] = ["The start, in seconds from 0."];
        }
        if (endSeconds is { } end && (!double.IsFinite(end) || end <= (startSeconds ?? 0)))
        {
            errors["endSeconds"] = ["The end, in seconds after the start."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        return await Call(async () =>
        {
            await using var audio = file!.OpenReadStream();
            var voice = await voices.AddVoiceAsync(label!.Trim(), audio, file.FileName, startSeconds, endSeconds, cancellationToken);
            return Results.Created($"/api/voices/{voice.Id}", voice);
        });
    }

    /// <summary>Passed through as it arrives, to listen to the voice again.</summary>
    private static async Task<IResult> VoiceAudioAsync(string id, VoiceClient voices, CancellationToken cancellationToken)
    {
        try
        {
            // Buffered rather than streamed through: Safari plays audio only from a source that answers range
            // requests, and a reference voice is at most 25 seconds, a few megabytes.
            using var response = await voices.VoiceAudioAsync(id, cancellationToken);
            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
            return Results.File(audio, type, enableRangeProcessing: true);
        }
        catch (VoiceServiceException exception)
        {
            return Problem(exception);
        }
    }

    private static async Task<IResult> AddVersionAsync(
        string run,
        string song,
        VersionRequest request,
        SongLibrary library,
        VoiceClient voices,
        VoiceConverter converter,
        IOptions<VoiceOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.ConversionConfigured)
        {
            return Results.Problem(title: "Voices are not configured (Voice:BaseUrl, the stem service and their keys).", statusCode: StatusCodes.Status501NotImplemented);
        }
        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        return await Call(async () =>
        {
            // Asked for here rather than when the version runs: a wrong id is refused now, and the label is kept.
            var voice = (await voices.ListVoicesAsync(cancellationToken)).FirstOrDefault(v => v.Id == request.VoiceId);
            if (voice is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["voiceId"] = ["No such reference voice."] });
            }
            var version = converter.Enqueue($"{run}/{song}", library.TitleOf(run, directory), voice, request);
            return Results.Accepted($"/api/songs/{run}/{song}/versions/{version.Id}/audio", version);
        });
    }

    private static IResult VersionAudio(string run, string song, string id, bool download, SqliteVersionStore store, SongLibrary library)
    {
        if (store.Get(id) is not { Stage: "done" } version || version.SongId != $"{run}/{song}" || !File.Exists(store.FilePath(id)))
        {
            return Results.NotFound();
        }
        var fileName = download && library.SongDirectory(run, song) is { } directory
            ? $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}-{LibraryEndpoints.FileName(version.VoiceLabel, "voice")}.flac"
            : null;
        return Results.File(store.FilePath(id), "audio/flac", fileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> Call(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (VoiceServiceException exception)
        {
            return Problem(exception);
        }
    }

    private static IResult Problem(VoiceServiceException exception) =>
        Results.Problem(title: exception.Message, statusCode: (int)exception.Status);
}
