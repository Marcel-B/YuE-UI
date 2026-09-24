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
/// On 24 GB a lyrics model and YuE2 do not fit side by side. So the YuE worker is stopped first when it idles
/// with its model loaded, the lyrics model is loaded for the request with a small context and unloaded right
/// after. Meanwhile <see cref="IsWriting"/> keeps new songs from starting. A model someone loaded in LM Studio
/// themselves is used as it is and left loaded.
/// </remarks>
public sealed partial class LyricsWriter(
    IHttpClientFactory httpClients,
    IOptions<LyricsOptions> options,
    ILmStudioStarter starter,
    WorkerHost worker,
    TimeProvider time,
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

    /// <summary>
    /// Starts a draft in the background; its progress and result arrive as <c>lyrics</c> events (see
    /// <see cref="LyricsState"/>), so a phone that locks meanwhile still gets it.
    /// </summary>
    /// <exception cref="LyricsBusyException">Another draft is in progress, or YuE2 is generating.</exception>
    public LyricsState Start(string keywords, string? style)
    {
        if (!_gate.Wait(0))
        {
            throw new LyricsBusyException("Lyrics are already being written.");
        }
        // Checked inside the gate: from here on no song can start (see IsWriting).
        if (worker.Snapshot().Worker.Busy)
        {
            _gate.Release();
            throw new LyricsBusyException("YuE2 is generating; the lyrics model would not fit into memory beside it.");
        }
        var state = new LyricsState { Id = Guid.NewGuid().ToString("N")[..12], UpdatedAt = time.GetUtcNow() };
        worker.UpdateLyrics(state);
        _ = Task.Run(() => RunAsync(state, keywords, style));
        return state;
    }

    private async Task RunAsync(LyricsState state, string keywords, string? style)
    {
        LyricsState result;
        try
        {
            result = state with { Stage = "done", Lyrics = await WriteAsync(keywords, style, CancellationToken.None) };
        }
        catch (LyricsUnavailableException exception)
        {
            result = state with { Stage = "failed", Message = exception.Message };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Drafting lyrics failed");
            result = state with { Stage = "failed", Message = exception.Message };
        }
        finally
        {
            // Before the result goes out, so that a song started in answer to it is not refused.
            _gate.Release();
        }
        worker.UpdateLyrics(result with { UpdatedAt = time.GetUtcNow() });
    }

    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or refused.</exception>
    private async Task<string> WriteAsync(string keywords, string? style, CancellationToken cancellationToken)
    {
        // Set once this call has loaded the model, so that it is unloaded again whatever happens next.
        string? unload = null;
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
            var (instance, loadedHere) = await LoadAsync(http, settings, cancellationToken);
            unload = loadedHere ? instance : null;
            return await CompleteAsync(http, settings, instance, keywords, style, logger, cancellationToken);
        }
        finally
        {
            if (unload is not null)
            {
                await UnloadAsync(http, unload);
            }
        }
    }

    /// <summary>
    /// Loads the model with <see cref="LyricsOptions.ContextLength"/> instead of leaving it to LM Studio's
    /// just-in-time loading, whose context can be the model's maximum.
    /// </summary>
    /// <returns>The instance to address, and whether it was loaded here (and so is unloaded after).</returns>
    private async Task<(string Instance, bool LoadedHere)> LoadAsync(HttpClient http, LyricsOptions settings, CancellationToken cancellationToken)
    {
        if (await LoadedInstanceAsync(http, settings.Model, cancellationToken) is { } loaded)
        {
            logger.LogInformation("Using {Instance}, which is already loaded in LM Studio", loaded);
            return (loaded, false);
        }

        var request = new JsonObject
        {
            ["model"] = settings.Model,
            ["context_length"] = settings.ContextLength,
            ["echo_load_config"] = true,
        };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("api/v1/models/load", Body(request), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering while loading {settings.Model}: {exception.Message}");
        }
        using (response)
        {
            var json = await ReadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // E.g. LM Studio's guardrails: "Model loading was stopped due to insufficient system resources …".
                throw new LyricsUnavailableException($"LM Studio could not load {settings.Model} ({(int)response.StatusCode}): {ErrorOf(json)}");
            }
            var instance = (json?["instance_id"] as JsonValue)?.GetValue<string>() ?? settings.Model;
            var context = json?["load_config"]?["context_length"]?.ToString();
            logger.LogInformation("LM Studio loaded {Instance} with a context of {Context} tokens in {Seconds} s",
                instance, context ?? "?", json?["load_time_seconds"]?.ToString() ?? "?");
            return (instance, true);
        }
    }

    /// <summary>An instance of the model that is already in memory, from LM Studio's model list; null if none or unknown.</summary>
    private static async Task<string?> LoadedInstanceAsync(HttpClient http, string model, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/v1/models", cancellationToken);
            var json = await ReadAsync(response, cancellationToken);
            var entry = (json?["models"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(m => m["key"]?.ToString() == model);
            return (entry?["loaded_instances"] as JsonArray)?.OfType<JsonObject>().Select(i => i["id"]?.ToString()).FirstOrDefault(id => id is not null);
        }
        catch (HttpRequestException)
        {
            return null;
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

    private static async Task<string> CompleteAsync(
        HttpClient http, LyricsOptions settings, string model, string keywords, string? style, ILogger logger, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = UserPrompt(keywords, style) }),
            ["temperature"] = settings.Temperature,
            // Room for a thinking model's reasoning before the lyrics, within the context it was loaded with.
            ["max_tokens"] = Math.Max(1024, settings.ContextLength / 2),
            ["stream"] = false,
            // LM Studio's own field: unload after this many idle seconds, should the model be loaded just in time.
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
            var json = await ReadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new LyricsUnavailableException($"LM Studio refused ({(int)response.StatusCode}): {ErrorOf(json)}");
            }
            var choice = json?["choices"]?[0];
            var content = (choice?["message"]?["content"] as JsonValue)?.GetValue<string>() ?? "";
            var lyrics = Clean(content);
            if (lyrics.Length > 0)
            {
                return lyrics;
            }

            // Thinking models (Gemma 4, Qwen 3) reason first; LM Studio returns that apart (reasoning_content) or
            // as <think> in the content. If the tokens run out while thinking, no lyrics are left.
            var finish = choice?["finish_reason"]?.ToString();
            var reasoning = (choice?["message"]?["reasoning_content"] ?? choice?["message"]?["reasoning"])?.ToString() ?? "";
            logger.LogWarning("No lyrics in the answer of {Model} (finish_reason {Finish}): {Answer}",
                model, finish, Truncate(json?.ToJsonString() ?? "", 2000));
            throw new LyricsUnavailableException(finish == "length" && (reasoning.Length > 0 || content.Contains("<think>", StringComparison.Ordinal))
                ? $"The model spent all its {request["max_tokens"]} tokens thinking and wrote no lyrics. Switch off thinking for it in LM Studio, or raise Lyrics:ContextLength."
                : $"The model returned no lyrics (finish_reason: {finish ?? "none"}{(reasoning.Length > 0 ? ", only reasoning" : "")}).");
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return body.Length > 0 ? JsonValue.Create(body) : null;
        }
    }

    /// <summary>LM Studio names the problem, e.g. a model id it does not know or too little memory.</summary>
    private static string ErrorOf(JsonNode? json) => json switch
    {
        JsonObject { } body when body["error"] is JsonObject error => error["message"]?.ToString() ?? error.ToJsonString(),
        JsonObject { } body when body["error"] is JsonNode error => error.ToString(),
        JsonNode node => node.ToString(),
        null => "no details",
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + " …";

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

    /// <summary>Best effort; a model loaded through the API has no idle TTL, so a failure is logged as a warning.</summary>
    private async Task UnloadAsync(HttpClient http, string model)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync("api/v1/models/unload", Body(new JsonObject { ["instance_id"] = model }), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LM Studio did not unload {Model} ({Status}); it stays in memory until unloaded in LM Studio", model, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("LM Studio did not unload {Model} ({Message}); it stays in memory until unloaded in LM Studio", model, exception.Message);
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
