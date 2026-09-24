using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YueUI.Api.Worker;

namespace YueUI.Api.Lyrics;

/// <summary>LM Studio could not be reached or started, or it refused the request.</summary>
public sealed class LyricsUnavailableException(string message) : Exception(message);

/// <summary>Another draft or a song is in progress; there is memory for one model at a time.</summary>
public sealed class LyricsBusyException(string message) : Exception(message);

/// <summary>
/// Drafts English lyrics in YuE2's format from a few keywords, with a language model in LM Studio (or any
/// OpenAI-compatible server).
/// </summary>
/// <remarks>
/// On 24 GB the lyrics model (about 15 GB) and YuE2 do not fit side by side. So the YuE worker is stopped first
/// when it idles with its model loaded, LM Studio loads the model just in time for the request, and it is
/// unloaded again right after. Meanwhile <see cref="IsWriting"/> keeps new songs from starting.
/// </remarks>
public sealed partial class LyricsWriter(
    IHttpClientFactory httpClients,
    IOptions<LyricsOptions> options,
    ILmStudioStarter starter,
    WorkerHost worker,
    ILogger<LyricsWriter> logger)
{
    public const string HttpClientName = "lyrics";

    /// <summary>What YuE2 sings well, from its documentation and the YuE v1 prompt guide.</summary>
    internal const string SystemPrompt = """
        You write song lyrics for YuE2, a model that turns lyrics and a style description into a sung song.
        Write in English. Answer with the lyrics only: no title, no explanations, no Markdown.

        Format:
        - Every section starts with its tag on a line of its own: [Verse], [Pre-Chorus], [Chorus], [Bridge] or [Outro].
        - Start with [Verse]. Do not use [Intro]; YuE2 handles it poorly.
        - One sung line per line. Exactly one empty line between sections.
        - No stage directions, no parentheses, no speaker names, no line numbers.

        Singability:
        - A section is sung in about 30 seconds: 4 lines, at most 6.
        - Keep the syllable count of the lines within a section even (6 to 10 syllables), with a clear stress pattern.
        - Plain, concrete words and open vowels on long notes; avoid tongue twisters and dense consonant clusters.
        - Rhyme the lines of a section. Repeat the chorus word for word each time it returns.

        Form: [Verse], [Chorus], [Verse], [Chorus], [Bridge], [Chorus] unless the request suggests another.
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A draft is in progress, so the lyrics model may be in memory: no song should start now.</summary>
    public bool IsWriting => _gate.CurrentCount == 0;

    /// <exception cref="LyricsBusyException">Another draft is in progress, or YuE2 is generating.</exception>
    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or refused.</exception>
    public async Task<string> WriteAsync(string keywords, string? style, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            throw new LyricsBusyException("Lyrics are already being written.");
        }
        // Checked inside the gate: from here on no song can start (see IsWriting).
        if (worker.Snapshot().Worker.Busy)
        {
            _gate.Release();
            throw new LyricsBusyException("YuE2 is generating; the lyrics model would not fit into memory beside it.");
        }
        var settings = options.Value;
        using var http = httpClients.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        try
        {
            if (worker.Snapshot().Worker.Status != WorkerStatus.Stopped)
            {
                logger.LogInformation("Stopping the idle YuE worker to make room for the lyrics model");
                await worker.ShutdownWorkerAsync();
            }
            await EnsureServerAsync(http, cancellationToken);
            return await CompleteAsync(http, settings, keywords, style, cancellationToken);
        }
        finally
        {
            await UnloadAsync(http, settings.Model);
            _gate.Release();
        }
    }

    private async Task EnsureServerAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (await AnswersAsync(http, cancellationToken))
        {
            return;
        }
        if (!await starter.StartAsync(cancellationToken))
        {
            throw new LyricsUnavailableException(
                $"LM Studio does not answer at {options.Value.BaseUrl}, and its command line tool was not found at {options.Value.LmsPath} to start it.");
        }
        if (!await AnswersAsync(http, cancellationToken))
        {
            throw new LyricsUnavailableException($"LM Studio was started but does not answer at {options.Value.BaseUrl}.");
        }
    }

    private static async Task<bool> AnswersAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("v1/models", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    private static async Task<string> CompleteAsync(HttpClient http, LyricsOptions settings, string keywords, string? style, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = UserPrompt(keywords, style) }),
            ["temperature"] = settings.Temperature,
            // Room for a thinking model's reasoning before the lyrics.
            ["max_tokens"] = 4096,
            ["stream"] = false,
            // LM Studio's own field: unload after this many idle seconds.
            ["ttl"] = settings.IdleTtlSeconds,
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("v1/chat/completions", Body(request), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering: {exception.Message}");
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(body);
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (!response.IsSuccessStatusCode)
            {
                // LM Studio names the problem, e.g. a model id it does not know.
                var reason = json?["error"] switch
                {
                    JsonObject error => error["message"]?.ToString(),
                    JsonNode error => error.ToString(),
                    null => null,
                } ?? body;
                throw new LyricsUnavailableException($"LM Studio refused ({(int)response.StatusCode}): {reason}");
            }
            var content = json?["choices"]?[0]?["message"]?["content"] as JsonValue;
            var lyrics = Clean(content?.GetValue<string>() ?? "");
            return lyrics.Length > 0 ? lyrics : throw new LyricsUnavailableException("The model returned no lyrics.");
        }
    }

    internal static string UserPrompt(string keywords, string? style) =>
        string.IsNullOrWhiteSpace(style)
            ? $"Write lyrics about: {keywords.Trim()}"
            : $"Write lyrics about: {keywords.Trim()}\nThe song's style (for mood and pacing, do not mention it): {style.Trim()}";

    /// <summary>What models add around the lyrics despite the instructions: reasoning, code fences, bold tags.</summary>
    internal static string Clean(string text)
    {
        text = Thinking().Replace(text.Replace("\r\n", "\n"), "");
        var lines = text.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .Select(line => Tag().Replace(line.Replace("**", "").TrimEnd(), "[$1]"));
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    /// <summary>Best effort: if it fails, LM Studio's idle TTL unloads the model a minute later.</summary>
    private async Task UnloadAsync(HttpClient http, string model)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync("api/v1/models/unload", Body(new JsonObject { ["instance_id"] = model }), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("LM Studio did not unload {Model}: {Status}", model, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            logger.LogInformation("LM Studio did not unload {Model}: {Message}", model, exception.Message);
        }
    }

    /// <summary>With a Content-Length: JsonContent would stream it chunked, which not every local server accepts.</summary>
    private static StringContent Body(JsonObject json) => new(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex Thinking();

    /// <summary>"Chorus:" or "[chorus]" on a line of its own → "[Chorus]"-style tag, kept as the model wrote the word.</summary>
    [GeneratedRegex(@"^\s*\[?\s*((?:Verse|Pre-Chorus|Chorus|Bridge|Outro|Intro|Hook)(?:\s*\d+)?)\s*\]?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
