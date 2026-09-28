using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Library;
using YueUI.Api.Voices;

namespace YueUI.Api;

/// <summary>
/// Songs split into their stems by StemMyWav, for the voices page: asked for per song, made one at a time in
/// <see cref="VoiceConverter"/>'s queue (the separation needs the memory YuE2 and Seed-VC need), kept per song.
/// </summary>
public static class StemEndpoints
{
    public static RouteGroupBuilder MapStemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/stems/models", ListModelsAsync);
        api.MapGet("/stems", (SqliteStemStore store) => Results.Ok(store.All()));
        api.MapPost("/songs/{run}/{song}/stems", Separate);
        // Range requests, as for the song itself: the page seeks and Safari plays only from a source that answers them.
        api.MapGet("/songs/{run}/{song}/stems/{id}/{name}", (string run, string song, string id, string name, bool? download, SqliteStemStore store, SongLibrary library) =>
            StemAudio(run, song, id, name, download == true, store, library));
        api.MapDelete("/songs/{run}/{song}/stems/{id}", (string run, string song, string id, SqliteStemStore store, VoiceConverter converter) =>
        {
            if (store.Get(id) is not { } set || set.SongId != $"{run}/{song}")
            {
                return Results.NotFound();
            }
            converter.DeleteStems(set);
            return Results.NoContent();
        });
        return api;
    }

    /// <summary>
    /// StemMyWav's catalog with this server's model marked as the default; only that model where the service lists
    /// none (its Mac API may not answer <c>api/models</c>, only its gateway surely does).
    /// </summary>
    private static async Task<IResult> ListModelsAsync(StemClient stems, IOptions<VoiceOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.StemsConfigured)
        {
            return NotConfigured();
        }
        var configured = options.Value.StemModel;
        var listed = await stems.ListModelsAsync(cancellationToken) ?? [];
        List<StemModel> models = [.. listed.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m with { IsDefault = m.Id == configured })];
        if (!models.Any(m => m.IsDefault))
        {
            models.Insert(0, new StemModel(configured, configured, IsDefault: true));
        }
        return Results.Ok(models);
    }

    private static IResult Separate(
        string run,
        string song,
        StemRequest? request,
        SongLibrary library,
        VoiceConverter converter,
        IOptions<VoiceOptions> options)
    {
        if (!options.Value.StemsConfigured)
        {
            return NotConfigured();
        }
        request ??= new StemRequest();
        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, "audio.flac")))
        {
            return Results.NotFound();
        }
        var set = converter.EnqueueStems($"{run}/{song}", library.TitleOf(run, directory), request.Model ?? options.Value.StemModel, request.Dereverb);
        return Results.Accepted($"/api/stems", set);
    }

    private static IResult StemAudio(string run, string song, string id, string name, bool download, SqliteStemStore store, SongLibrary library)
    {
        if (store.Get(id) is not { Stage: "done" } set || set.SongId != $"{run}/{song}"
            || store.FilePath(set, name) is not { } path || !File.Exists(path))
        {
            return Results.NotFound();
        }
        var extension = Path.GetExtension(path);
        var type = extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? "audio/flac" : "audio/wav";
        var fileName = download && library.SongDirectory(run, song) is { } directory
            ? $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}-{LibraryEndpoints.FileName(name, "stem")}{extension}"
            : null;
        return Results.File(path, type, fileName, enableRangeProcessing: true);
    }

    private static IResult NotConfigured() =>
        Results.Problem(title: "No stem service is configured (Voice:StemsBaseUrl and its key).", statusCode: StatusCodes.Status501NotImplemented);
}
