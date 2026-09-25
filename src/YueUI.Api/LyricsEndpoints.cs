using YueUI.Api.Lyrics;

namespace YueUI.Api;

/// <param name="Keywords">Topic, images, story: what the song is about.</param>
/// <param name="Style">The form's style prompt, so the words fit the mood; optional.</param>
/// <param name="Language">The language the lyrics are written in; English when left out.</param>
/// <param name="Model">A model id from <c>GET /api/lyrics/models</c>; the configured one when left out.</param>
public sealed record LyricsRequest(string? Keywords, string? Style, LyricsLanguage? Language = null, string? Model = null);

/// <summary>
/// Drafting lyrics with a local language model (LM Studio), see <see cref="LyricsWriter"/>. Like the worker's
/// commands it answers 202; the draft arrives as a <c>lyrics</c> event.
/// </summary>
public static class LyricsEndpoints
{
    public const int MaxKeywordsLength = 1000;

    /// <summary>LM Studio's keys are publisher/name, far shorter; this only bounds what is passed on.</summary>
    public const int MaxModelLength = 200;

    public static RouteGroupBuilder MapLyricsEndpoints(this RouteGroupBuilder api)
    {
        // 503 when LM Studio neither answers nor can be started: the form then drafts with the configured model.
        api.MapGet("/lyrics/models", async (LyricsWriter writer, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await writer.ListModelsAsync(cancellationToken));
            }
            catch (LyricsUnavailableException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapPost("/lyrics", (LyricsRequest request, LyricsWriter writer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Keywords) || request.Keywords.Length > MaxKeywordsLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters."],
                });
            }
            // Unknown ids are left to LM Studio, which refuses them with its own message.
            if (request.Model is { } model && (model.Length > MaxModelLength || model.Any(char.IsControl)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["model"] = [$"A model id as GET /api/lyrics/models lists it, up to {MaxModelLength} characters."],
                });
            }
            try
            {
                return Results.Accepted(value: writer.Start(request.Keywords, request.Style, request.Language ?? LyricsLanguage.English, request.Model));
            }
            catch (LyricsBusyException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        return api;
    }
}
