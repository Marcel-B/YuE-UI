using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Library;
using YueUI.Api.Logic;

namespace YueUI.Api;

/// <param name="Configured">A yue-to-logic-pro server is set under <c>Logic:BaseUrl</c>.</param>
public sealed record LogicExportInfo(bool Configured);

/// <summary>
/// A song as a Logic Pro project: its <c>audio.flac</c> and <c>score.abc</c> go to yue-to-logic-pro, server to
/// server, and the <c>.logicx</c> ZIP it builds comes back to the browser. The files are on this Mac already, so
/// the phone does not have to download 50 MB of FLAC only to upload them again.
/// </summary>
public static class LogicEndpoints
{
    public const string HttpClientName = "logic";

    /// <summary>yue-to-logic-pro's warnings of an export (compact JSON), passed on to the browser.</summary>
    public const string DiagnosticsHeader = "X-YueToLogic-Diagnostics";

    public static RouteGroupBuilder MapLogicEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/logic", (IOptions<LogicOptions> options) => new LogicExportInfo(options.Value.BaseUri is not null));
        api.MapGet("/songs/{run}/{song}/logic", ExportAsync);
        return api;
    }

    private static async Task<IResult> ExportAsync(
        string run,
        string song,
        SongLibrary library,
        IOptions<LogicOptions> options,
        IHttpClientFactory httpClients,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (options.Value.BaseUri is not { } baseUri)
        {
            return Results.Problem(title: "No yue-to-logic-pro server is configured (Logic:BaseUrl).", statusCode: StatusCodes.Status501NotImplemented);
        }
        if (library.SongDirectory(run, song) is not { } directory
            || !File.Exists(Path.Combine(directory, "audio.flac"))
            || !File.Exists(Path.Combine(directory, "score.abc")))
        {
            return Results.NotFound();
        }

        var name = $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}";
        await using var score = File.OpenRead(Path.Combine(directory, "score.abc"));
        await using var audio = File.OpenRead(Path.Combine(directory, "audio.flac"));
        using var form = new MultipartFormDataContent
        {
            { Upload(score, "text/plain"), "file", "score.abc" },
            { Upload(audio, "audio/flac"), "audio", "audio.flac" },
            { new StringContent(name), "name" },
            { new StringContent(options.Value.SplitSections ? "true" : "false"), "splitSections" },
        };

        var client = httpClients.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/convert/logic")) { Content = form };
            // Headers only: the ZIP is as large as the FLAC and goes on to the browser as it arrives.
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            loggers.CreateLogger(typeof(LogicEndpoints)).LogWarning(exception, "yue-to-logic-pro at {BaseUri} did not answer", baseUri);
            return Results.Problem(title: $"yue-to-logic-pro at {baseUri} could not be reached.", statusCode: StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(title: "yue-to-logic-pro took too long to build the project.", statusCode: StatusCodes.Status504GatewayTimeout);
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                return await RefusalAsync(response, cancellationToken);
            }
        }

        context.Response.RegisterForDispose(response);
        if (response.Headers.TryGetValues(DiagnosticsHeader, out var diagnostics))
        {
            context.Response.Headers[DiagnosticsHeader] = diagnostics.ToArray();
        }
        return Results.Stream(await response.Content.ReadAsStreamAsync(cancellationToken), "application/zip", $"{name}.logicx.zip");
    }

    private static StreamContent Upload(Stream stream, string contentType) =>
        new(stream) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } };

    /// <summary>
    /// A 422 means the song itself cannot become a project (a score it cannot read, audio that is not 48 kHz);
    /// its diagnostics say why and are passed on. Anything else is yue-to-logic-pro's or this server's fault.
    /// </summary>
    private static async Task<IResult> RefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        JsonNode? body = null;
        try
        {
            body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var messages = (body?["diagnostics"] as JsonArray ?? [])
                .Where(d => string.Equals((string?)d?["severity"], "Error", StringComparison.OrdinalIgnoreCase))
                .Select(d => (string?)d?["message"])
                .OfType<string>()
                .ToList();
            // The browser shows the detail alone, so it carries the whole sentence.
            const string title = "yue-to-logic-pro cannot turn this song into a Logic project.";
            return Results.Problem(
                title: title,
                detail: string.Join(" ", [title, .. messages]),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var answered = $"yue-to-logic-pro answered {(int)response.StatusCode}.";
        var reason = (string?)body?["detail"] ?? (string?)body?["title"];
        return Results.Problem(
            title: answered,
            detail: reason is null ? answered : $"{answered} {reason}",
            statusCode: StatusCodes.Status502BadGateway);
    }
}
