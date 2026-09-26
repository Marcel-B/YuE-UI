using YueUI.Api.Lyrics;
using YueUI.Api.Worker;

namespace YueUI.Api;

/// <param name="Keywords">Topic, images, story: what the song is about. Optional with a photo.</param>
/// <param name="Style">The form's style prompt, so the words fit the mood; optional.</param>
/// <param name="Language">The language the lyrics are written in; English when left out.</param>
/// <param name="Model">A model id from <c>GET /api/lyrics/models</c>; the configured one when left out.</param>
/// <param name="Image">
/// A photo the song is about, as a <c>data:image/jpeg;base64,…</c> URL (PNG and WebP too); the model must be able to see.
/// </param>
/// <param name="Lyrics">
/// Lyrics to revise instead of writing new ones, typically the last draft; <paramref name="Instruction"/> says how.
/// </param>
/// <param name="Instruction">What to change in <paramref name="Lyrics"/>, e.g. "make the chorus catchier".</param>
public sealed record LyricsRequest(
    string? Keywords,
    string? Style,
    LyricsLanguage? Language = null,
    string? Model = null,
    string? Image = null,
    string? Lyrics = null,
    string? Instruction = null);

/// <summary>
/// Drafting lyrics with a local language model (LM Studio), see <see cref="LyricsWriter"/>. Like the worker's
/// commands it answers 202; the draft arrives as a <c>lyrics</c> event.
/// </summary>
public static class LyricsEndpoints
{
    public const int MaxKeywordsLength = 1000;

    /// <summary>LM Studio's keys are publisher/name, far shorter; this only bounds what is passed on.</summary>
    public const int MaxModelLength = 200;

    /// <summary>A song YuE2 can sing is far shorter (its token limit ends it after a few minutes).</summary>
    public const int MaxLyricsLength = 10_000;

    /// <summary>
    /// The browser scales a photo down to 1024 pixels before sending, some 200 KB; this leaves room for a PNG
    /// sent as it is, and still keeps a phone's 12-megapixel original out.
    /// </summary>
    public const int MaxImageLength = 8 * 1024 * 1024;

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
            if (request.Image is { } image && !IsImage(image))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["image"] = [$"A JPEG, PNG or WebP photo as a base64 data URL, up to {MaxImageLength / 1024 / 1024} MB."],
                });
            }
            if (request.Lyrics is not null || request.Instruction is not null)
            {
                return Revise(request, writer);
            }
            if ((string.IsNullOrWhiteSpace(request.Keywords) && request.Image is null) || request.Keywords?.Length > MaxKeywordsLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters; optional with a photo."],
                });
            }
            if (InvalidModel(request.Model) is { } invalid)
            {
                return invalid;
            }
            return Start(() => writer.Start(request.Keywords, request.Style, request.Language ?? LyricsLanguage.English, request.Model, request.Image));
        });
        return api;
    }

    /// <summary>
    /// A revision works on the lyrics as they are rather than drafting anew; the keywords, if sent, tell the model what
    /// the song is about. A photo would add nothing the lyrics do not already carry, and cost a vision model.
    /// </summary>
    private static IResult Revise(LyricsRequest request, LyricsWriter writer)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Lyrics) || request.Lyrics.Length > MaxLyricsLength)
        {
            errors["lyrics"] = [$"The lyrics to revise, up to {MaxLyricsLength} characters."];
        }
        if (string.IsNullOrWhiteSpace(request.Instruction) || request.Instruction.Length > MaxKeywordsLength)
        {
            errors["instruction"] = [$"What to change in the lyrics, up to {MaxKeywordsLength} characters."];
        }
        if (request.Keywords?.Length > MaxKeywordsLength)
        {
            errors["keywords"] = [$"What the song is about, up to {MaxKeywordsLength} characters."];
        }
        if (request.Image is not null)
        {
            errors["image"] = ["A revision works on the lyrics alone; send the photo with a new draft."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }
        if (InvalidModel(request.Model) is { } invalid)
        {
            return invalid;
        }
        var revision = new LyricsRevision(request.Lyrics!, request.Instruction!);
        return Start(() => writer.Start(request.Keywords, request.Style, request.Language ?? LyricsLanguage.English, request.Model, revision: revision));
    }

    /// <summary>Unknown ids are left to LM Studio, which refuses them with its own message.</summary>
    private static IResult? InvalidModel(string? model) =>
        model is not null && (model.Length > MaxModelLength || model.Any(char.IsControl))
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["model"] = [$"A model id as GET /api/lyrics/models lists it, up to {MaxModelLength} characters."],
            })
            : null;

    private static IResult Start(Func<LyricsState> start)
    {
        try
        {
            return Results.Accepted(value: start());
        }
        catch (LyricsBusyException exception)
        {
            return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Checked here so that LM Studio is not loaded for a request it cannot read.</summary>
    private static bool IsImage(string image)
    {
        if (image.Length > MaxImageLength
            || !(image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
                || image.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
                || image.StartsWith("data:image/webp;base64,", StringComparison.Ordinal)))
        {
            return false;
        }
        var data = image.AsSpan(image.IndexOf(',') + 1);
        return data.Length > 0 && System.Buffers.Text.Base64.IsValid(data);
    }
}
