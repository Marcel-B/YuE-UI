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
/// Drafts English or German lyrics in YuE2's format from a few keywords, with a language model in LM Studio (or any
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
    internal static string SystemPrompt(LyricsLanguage language) => $"""
        You write song lyrics for YuE2, a model that turns lyrics and a style description into a sung song.
        {LanguageRule(language)} Answer with the lyrics only: no title, no explanations, no Markdown.

        Format:
        - Every section starts with its tag on a line of its own: [Verse], [Pre-Chorus], [Chorus], [Bridge] or [Outro].
        - Keep these tags in English whatever the language of the lyrics; YuE2 reads the song's form from them.
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

    /// <remarks>
    /// German gets its own hints: small models tend to pad lines with long compounds that do not fit a melody, and
    /// to fall back into English phrases.
    /// </remarks>
    private static string LanguageRule(LyricsLanguage language) => language switch
    {
        LyricsLanguage.German => "Write in German, every sung line, with natural German word order and correct umlauts (ä, ö, ü, ß); " +
            "no English phrases. Prefer short words to long compounds, and rhymes that sound natural in German.",
        _ => "Write in English.",
    };

    /// <summary>Room for system prompt, keywords and style; the system prompt alone is about 300 tokens.</summary>
    private const int PromptTokens = 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A draft is in progress, so the lyrics model may be in memory: no song should start now.</summary>
    public bool IsWriting => _gate.CurrentCount == 0;

    /// <summary>
    /// Starts a draft in the background; its progress and result arrive as <c>lyrics</c> events (see
    /// <see cref="LyricsState"/>), so a phone that locks meanwhile still gets it.
    /// </summary>
    /// <param name="model">A model id from <see cref="ListModelsAsync"/>; null for <see cref="LyricsOptions.Model"/>.</param>
    /// <exception cref="LyricsBusyException">Another draft is in progress, or YuE2 is generating.</exception>
    public LyricsState Start(string keywords, string? style, LyricsLanguage language = LyricsLanguage.English, string? model = null)
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
        _ = Task.Run(() => RunAsync(state, keywords, style, language, string.IsNullOrWhiteSpace(model) ? options.Value.Model : model.Trim()));
        return state;
    }

    private async Task RunAsync(LyricsState state, string keywords, string? style, LyricsLanguage language, string model)
    {
        LyricsState result;
        try
        {
            result = state with { Stage = "done", Lyrics = await WriteAsync(model, keywords, style, language, CancellationToken.None) };
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
    private async Task<string> WriteAsync(string model, string keywords, string? style, LyricsLanguage language, CancellationToken cancellationToken)
    {
        // Set once this call has loaded the model, so that it is unloaded again whatever happens next.
        string? unload = null;
        var settings = options.Value;
        using var http = CreateClient();
        try
        {
            if (worker.Snapshot().Worker.Status != WorkerStatus.Stopped)
            {
                logger.LogInformation("Stopping the idle YuE worker to make room for the lyrics model");
                await worker.ShutdownWorkerAsync();
            }
            await EnsureServerAsync(http, cancellationToken);
            var (instance, loadedHere, context) = await LoadAsync(http, model, settings, cancellationToken);
            unload = loadedHere ? instance : null;
            return await CompleteAsync(http, settings, instance, context, keywords, style, language, logger, cancellationToken);
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
    /// <remarks>
    /// LM Studio's guardrails refuse a load whose estimate (weights plus the cache for the whole context) exceeds the
    /// free memory. A dense model loads less easily than Gemma 4's mixture of experts: Qwen 3.8 27B needs all its
    /// weights and a cache of several GB for 30000 tokens, which on 24 GB is refused as soon as a few apps are open.
    /// The guardrail is right to refuse (it guards against the swap freeze), so it is not switched off; instead what
    /// is in the way goes first: other models still loaded in LM Studio, then half the context each time down to
    /// <see cref="LyricsOptions.MinContextLength"/>. The draft then has less room for reasoning, which is still
    /// better than none.
    /// </remarks>
    /// <returns>The instance to address, whether it was loaded here (and so is unloaded after), and its context.</returns>
    private async Task<(string Instance, bool LoadedHere, int Context)> LoadAsync(HttpClient http, string model, LyricsOptions settings, CancellationToken cancellationToken)
    {
        var loaded = await LoadedInstancesAsync(http, cancellationToken);
        if (loaded.FirstOrDefault(i => i.Model == model) is { Instance: { } instance })
        {
            logger.LogInformation("Using {Instance}, which is already loaded in LM Studio", instance);
            return (instance, false, settings.ContextLength);
        }

        var context = settings.ContextLength;
        var othersUnloaded = false;
        while (true)
        {
            var (loadedInstance, loadedContext, refusal) = await TryLoadAsync(http, model, context, cancellationToken);
            if (loadedInstance is not null)
            {
                return (loadedInstance, true, loadedContext);
            }
            if (!refusal!.Contains("insufficient system resources", StringComparison.OrdinalIgnoreCase))
            {
                throw new LyricsUnavailableException($"LM Studio could not load {model}: {refusal}");
            }
            if (!othersUnloaded && loaded.Count > 0)
            {
                othersUnloaded = true;
                foreach (var other in loaded)
                {
                    logger.LogInformation("Unloading {Instance} to make room for {Model}", other.Instance, model);
                    await UnloadAsync(http, other.Instance);
                }
                continue;
            }
            var smaller = Math.Max(settings.MinContextLength, context / 2);
            if (smaller >= context)
            {
                throw new LyricsUnavailableException($"LM Studio could not load {model}, not even with a context of {context} tokens; close other apps or pick a smaller model. {refusal}");
            }
            logger.LogInformation("LM Studio refused {Model} with a context of {Context} tokens for lack of memory; trying {Smaller}", model, context, smaller);
            context = smaller;
        }
    }

    /// <returns>The instance and its context if LM Studio loaded the model, else its refusal.</returns>
    private async Task<(string? Instance, int Context, string? Refusal)> TryLoadAsync(HttpClient http, string model, int context, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["context_length"] = context,
            ["echo_load_config"] = true,
        };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("api/v1/models/load", Body(request), cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LyricsUnavailableException($"LM Studio stopped answering while loading {model}: {exception.Message}");
        }
        using (response)
        {
            var json = await ReadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // E.g. LM Studio's guardrails: "Model loading was stopped due to insufficient system resources …".
                return (null, 0, $"({(int)response.StatusCode}) {ErrorOf(json)}");
            }
            var instance = (json?["instance_id"] as JsonValue)?.GetValue<string>() ?? model;
            var loadedContext = (json?["load_config"]?["context_length"] as JsonValue)?.TryGetValue<int>(out var echoed) == true ? echoed : context;
            logger.LogInformation("LM Studio loaded {Instance} with a context of {Context} tokens in {Seconds} s",
                instance, loadedContext, json?["load_time_seconds"]?.ToString() ?? "?");
            return (instance, loadedContext, null);
        }
    }

    /// <summary>
    /// The models the picker offers: what LM Studio has downloaded, whether loaded or not, without embedding models.
    /// Starts LM Studio's server like a draft would; that loads no model.
    /// </summary>
    /// <exception cref="LyricsUnavailableException">LM Studio could not be reached or started.</exception>
    public async Task<LyricsModels> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var http = CreateClient();
        await EnsureServerAsync(http, cancellationToken);
        var models = await NativeModelsAsync(http, cancellationToken) ?? await OpenAiModelsAsync(http, cancellationToken);
        // The configured model stays choosable even when LM Studio does not list it, e.g. while it is still downloading.
        if (!models.Any(m => m.Id == settings.Model))
        {
            models.Insert(0, new LyricsModel(settings.Model, settings.Model, null, false));
        }
        return new LyricsModels(settings.Model, models);
    }

    /// <summary>
    /// LM Studio's own list, with names and sizes: the size is what decides whether a model fits beside the apps on
    /// 24 GB. Null for a server without it (another OpenAI-compatible one).
    /// </summary>
    private static async Task<List<LyricsModel>?> NativeModelsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/v1/models", cancellationToken);
            if (!response.IsSuccessStatusCode || await ReadAsync(response, cancellationToken) is not JsonObject { } json
                || json["models"] is not JsonArray models)
            {
                return null;
            }
            return [.. models.OfType<JsonObject>()
                .Where(m => m["key"] is JsonValue && m["type"]?.ToString() != "embedding")
                .Select(m => new LyricsModel(
                    m["key"]!.ToString(),
                    m["display_name"]?.ToString() is { Length: > 0 } name ? name : m["key"]!.ToString(),
                    (m["size_bytes"] as JsonValue)?.TryGetValue<long>(out var size) == true ? size : null,
                    (m["loaded_instances"] as JsonArray)?.Count > 0))];
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>The OpenAI-compatible list: ids only. LM Studio lists downloaded models there too.</summary>
    private static async Task<List<LyricsModel>> OpenAiModelsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("v1/models", cancellationToken);
            var json = await ReadAsync(response, cancellationToken);
            return [.. ((json?["data"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Select(m => m["id"]?.ToString())
                .OfType<string>()
                .Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase))
                .Select(id => new LyricsModel(id, id, null, false))];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private HttpClient CreateClient()
    {
        var settings = options.Value;
        var http = httpClients.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        }
        return http;
    }

    /// <summary>The language models in memory, from LM Studio's model list; empty if none or unknown.</summary>
    private static async Task<List<(string Model, string Instance)>> LoadedInstancesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/v1/models", cancellationToken);
            var json = await ReadAsync(response, cancellationToken);
            return [.. ((json?["models"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Where(m => m["key"] is JsonValue && m["type"]?.ToString() != "embedding")
                .SelectMany(m => ((m["loaded_instances"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    .Select(i => i["id"]?.ToString())
                    .OfType<string>()
                    .Select(id => (m["key"]!.ToString(), id)))];
        }
        catch (HttpRequestException)
        {
            return [];
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
        HttpClient http, LyricsOptions settings, string model, int context, string keywords, string? style, LyricsLanguage language, ILogger logger,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(language) },
                new JsonObject { ["role"] = "user", ["content"] = UserPrompt(keywords, style) }),
            ["temperature"] = settings.Temperature,
            // Thinking models reason before the lyrics, and at length: Gemma 4 26B counts every line's syllables,
            // well over 4000 tokens. So the answer gets the whole context it was loaded with except the prompt,
            // which may be less than configured (see LoadAsync).
            // LM Studio's reasoning "off" is no way out for Gemma 4: it thinks anyway, unmarked in the answer.
            ["max_tokens"] = Math.Max(1024, context - PromptTokens),
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
                ? $"The model spent all its {request["max_tokens"]} tokens thinking and wrote no lyrics. Raise Lyrics:ContextLength, or pick a model that thinks less."
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
