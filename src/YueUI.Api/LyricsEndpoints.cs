using YueUI.Api.Lyrics;

namespace YueUI.Api;

/// <param name="Keywords">Topic, images, story: what the song is about.</param>
/// <param name="Style">The form's style prompt, so the words fit the mood; optional.</param>
public sealed record LyricsRequest(string? Keywords, string? Style);

/// <summary>
/// Drafting lyrics with a local language model (LM Studio), see <see cref="LyricsWriter"/>. Like the worker's
/// commands it answers 202; the draft arrives as a <c>lyrics</c> event.
/// </summary>
public static class LyricsEndpoints
{
    public const int MaxKeywordsLength = 1000;

    public static RouteGroupBuilder MapLyricsEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/lyrics", (LyricsRequest request, LyricsWriter writer) =>
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
                return Results.Accepted(value: writer.Start(request.Keywords, request.Style));
            }
            catch (LyricsBusyException exception)
            {
                return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
        return api;
    }
}
