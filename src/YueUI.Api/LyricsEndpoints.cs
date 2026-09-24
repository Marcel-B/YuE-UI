using YueUI.Api.Lyrics;

namespace YueUI.Api;

/// <param name="Keywords">Topic, images, story: what the song is about.</param>
/// <param name="Style">The form's style prompt, so the words fit the mood; optional.</param>
public sealed record LyricsRequest(string? Keywords, string? Style);

public sealed record LyricsDraft(string Lyrics);

/// <summary>Drafting lyrics with a local language model (LM Studio), see <see cref="LyricsWriter"/>.</summary>
public static class LyricsEndpoints
{
    public const int MaxKeywordsLength = 1000;

    public static RouteGroupBuilder MapLyricsEndpoints(this RouteGroupBuilder api)
    {
        // The answer takes a while (loading the model alone is several seconds); the phone simply waits for it.
        api.MapPost("/lyrics", async (LyricsRequest request, LyricsWriter writer, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Keywords) || request.Keywords.Length > MaxKeywordsLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters."],
                });
            }
            try
            {
                return Results.Ok(new LyricsDraft(await writer.WriteAsync(request.Keywords, request.Style, cancellationToken)));
            }
            catch (LyricsBusyException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (LyricsUnavailableException exception)
            {
                return Results.Problem(title: "LM Studio is not available", detail: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        return api;
    }
}
