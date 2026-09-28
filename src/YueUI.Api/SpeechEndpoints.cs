using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Speech;

namespace YueUI.Api;

/// <summary>
/// The speech lab (<see cref="SpeechLab"/>): record a voice in the browser, have text spoken by local models with it,
/// and compare the takes. Takes are spoken in the background; their progress arrives as <c>speech</c> events.
/// </summary>
public static class SpeechEndpoints
{
    /// <summary>A browser recording of half a minute is well under a megabyte; an uploaded WAV may be larger.</summary>
    public const long MaxRecordingBytes = 50L * 1024 * 1024;

    /// <summary>Anything shorter gives the models too little to go on.</summary>
    public const double MinVoiceSeconds = 2;

    public static RouteGroupBuilder MapSpeechEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/speech", (IOptions<SpeechOptions> options, ISpeechEngine engine, SqliteSpeechStore store) =>
            new SpeechInfo(
                engine.Installed,
                options.Value.ResolvedPython,
                engine.CanPrepareVoices,
                [.. options.Value.ResolvedModels.Select(m => new SpeechModelInfo(m.Id, m.Label, m.Repo, m.DownloadGb, m.Clones, m.License, m.Note, engine.Downloaded(m)))],
                store.Voices(),
                store.Takes()));

        api.MapPost("/speech/voices", AddVoiceAsync)
            // Posted from a plain form; there is no login and no cookie that a forged request could use.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRecordingBytes + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxRecordingBytes);
        api.MapGet("/speech/voices/{id}/audio", (string id, SqliteSpeechStore store) =>
            store.Voice(id) is not null && File.Exists(store.VoicePath(id))
                ? Results.File(store.VoicePath(id), "audio/wav", enableRangeProcessing: true)
                : Results.NotFound());
        api.MapDelete("/speech/voices/{id}", (string id, SqliteSpeechStore store) =>
            store.RemoveVoice(id) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/speech/takes", AddTakes);
        // Range requests: Safari plays audio only from a source that answers them.
        api.MapGet("/speech/takes/{id}/audio", (string id, bool? download, SqliteSpeechStore store) =>
            store.Take(id) is { Stage: "done" } take && File.Exists(store.TakePath(id))
                ? Results.File(store.TakePath(id), "audio/wav", download == true ? $"{LibraryEndpoints.FileName(take.ModelLabel, "take")}-{id[..8]}.wav" : null, enableRangeProcessing: true)
                : Results.NotFound());
        api.MapDelete("/speech/takes/{id}", (string id, SqliteSpeechStore store, SpeechLab lab) =>
        {
            if (store.Take(id) is not { } take)
            {
                return Results.NotFound();
            }
            lab.Delete(take);
            return Results.NoContent();
        });
        return api;
    }

    private static async Task<IResult> AddVoiceAsync(
        [FromForm] string? label,
        [FromForm] string? transcript,
        IFormFile? file,
        SqliteSpeechStore store,
        ISpeechEngine engine,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 100)
        {
            errors["label"] = ["A name for the voice, up to 100 characters."];
        }
        if (transcript?.Trim().Length > 1000)
        {
            errors["transcript"] = ["What is said in the recording, up to 1000 characters."];
        }
        if (file is null || file.Length == 0)
        {
            errors["file"] = ["A recording of the voice."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.CanPrepareVoices)
        {
            return Results.Problem(title: "ffmpeg is not installed; recordings cannot be converted.", statusCode: StatusCodes.Status501NotImplemented);
        }

        var id = Guid.NewGuid().ToString("N");
        var upload = Path.Combine(Path.GetTempPath(), $"yueui-speech-upload-{id}{Extension(file!.FileName)}");
        var target = store.VoicePath(id);
        try
        {
            await using (var stream = File.Create(upload))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await engine.PrepareVoiceAsync(upload, target, cancellationToken);
            var seconds = WavInfo.Seconds(target) ?? 0;
            if (seconds < MinVoiceSeconds)
            {
                File.Delete(target);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = [$"Only {seconds:0.#} seconds of speech; record at least {MinVoiceSeconds:0} (5 to 15 are best)."],
                });
            }
            var voice = new SpeechVoice(id, label!.Trim(), transcript?.Trim() ?? "", seconds, time.GetUtcNow());
            store.AddVoice(voice);
            return Results.Created($"/api/speech/voices/{id}/audio", voice);
        }
        catch (SpeechException exception)
        {
            File.Delete(target);
            return Results.Problem(title: exception.Message, statusCode: exception.Status);
        }
        finally
        {
            File.Delete(upload);
        }
    }

    private static IResult AddTakes(SpeechRequest request, SqliteSpeechStore store, ISpeechEngine engine, SpeechLab lab, IOptions<SpeechOptions> options)
    {
        var offered = options.Value.ResolvedModels;
        var errors = request.Validate(offered);
        var voice = request.VoiceId is { Length: > 0 } voiceId ? store.Voice(voiceId) : null;
        if (request.VoiceId is { Length: > 0 } && voice is null)
        {
            errors["voiceId"] = ["No such recorded voice."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (!engine.Installed)
        {
            return Results.Problem(
                title: $"mlx-audio is not installed ({options.Value.ResolvedPython}); run deploy/install-speech.sh on the Mac.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        // In the order the models are offered, whatever order they were ticked in: the page compares them side by side.
        var models = offered.Where(m => request.Models!.Contains(m.Id));
        return Results.Accepted("/api/speech", lab.Enqueue(request.Text!.Trim(), voice, models));
    }

    /// <summary>ffmpeg reads the container from the content, but a known extension saves it guessing.</summary>
    private static string Extension(string fileName) =>
        Path.GetExtension(fileName) is { Length: > 1 and <= 6 } extension && extension[1..].All(char.IsAsciiLetterOrDigit) ? extension : "";
}
