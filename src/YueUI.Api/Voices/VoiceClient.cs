using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Voices;

/// <summary>A refusal by ChangeMyVoice or StemMyWav, with their own reason; the status is passed on to the browser.</summary>
public sealed class VoiceServiceException(string message, HttpStatusCode status = HttpStatusCode.BadGateway) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <param name="Status">ChangeMyVoice's: QUEUED, RUNNING, COMPLETED, FAILED or CANCELLED.</param>
/// <param name="EstimatedSeconds">How long ChangeMyVoice expects the conversion to take.</param>
public sealed record VoiceJob(string Id, string Status, string? Error, DateTimeOffset? StartedAt, double EstimatedSeconds);

/// <summary>ChangeMyVoice's Mac API (<c>/api/v1</c>), with the key from <see cref="VoiceOptions"/>.</summary>
public sealed class VoiceClient(IHttpClientFactory clients, IOptions<VoiceOptions> options)
{
    public const string HttpClientName = "voice";

    public async Task<IReadOnlyList<ReferenceVoice>> ListVoicesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/voices", null, cancellationToken);
        var voices = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken) ?? [];
        return [.. voices.OfType<JsonObject>().Select(ToVoice)];
    }

    /// <param name="startSeconds">Where the part to keep begins, null for the beginning.</param>
    /// <param name="endSeconds">Where it ends, null for the end; ChangeMyVoice keeps at most 25 seconds from the start.</param>
    public async Task<ReferenceVoice> AddVoiceAsync(
        string label, Stream audio, string fileName, double? startSeconds, double? endSeconds, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(label), "label" },
            { new StreamContent(audio), "file", fileName },
        };
        // Sent only when set, with a point as ChangeMyVoice expects: a ChangeMyVoice from before trimming ignores them.
        if (startSeconds is { } start)
        {
            form.Add(new StringContent(start.ToString("0.###", CultureInfo.InvariantCulture)), "startSeconds");
        }
        if (endSeconds is { } end)
        {
            form.Add(new StringContent(end.ToString("0.###", CultureInfo.InvariantCulture)), "endSeconds");
        }
        using var response = await SendAsync(HttpMethod.Post, "api/v1/voices", form, cancellationToken);
        return ToVoice(await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? []);
    }

    /// <summary>The stored recording (mono WAV, 44.1 kHz, at most 25 s); the caller disposes the response.</summary>
    public Task<HttpResponseMessage> VoiceAudioAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"api/v1/voices/{Uri.EscapeDataString(id)}/audio", null, cancellationToken, headersOnly: true);

    public async Task DeleteVoiceAsync(string id, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"api/v1/voices/{Uri.EscapeDataString(id)}", null, cancellationToken);
    }

    /// <summary>Hands the vocals over; the singing path (F0 conditioning) is ChangeMyVoice's default.</summary>
    public async Task<VoiceJob> StartJobAsync(VersionState version, string vocals, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(vocals);
        var audio = new StreamContent(source);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(version.VoiceId), "voiceId" },
            { audio, "source", "vocals.wav" },
            { new StringContent(version.DiffusionSteps.ToString(CultureInfo.InvariantCulture)), "diffusionSteps" },
            { new StringContent(version.Strength.ToString("0.###", CultureInfo.InvariantCulture)), "inferenceCfgRate" },
            // 48 kHz like the stems, so the mix needs no resampling of the instrumental.
            { new StringContent("48000"), "outputSampleRate" },
        };
        // Sent only when set: a ChangeMyVoice from before semiToneShift would otherwise refuse every job.
        if (version.SemiToneShift != 0)
        {
            form.Add(new StringContent(version.SemiToneShift.ToString(CultureInfo.InvariantCulture)), "semiToneShift");
        }
        using var response = await SendAsync(HttpMethod.Post, "api/v1/jobs", form, cancellationToken);
        return ToJob(await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? []);
    }

    public async Task<VoiceJob> GetJobAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/jobs/{Uri.EscapeDataString(id)}", null, cancellationToken);
        return ToJob(await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? []);
    }

    public async Task DownloadResultAsync(string id, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/jobs/{Uri.EscapeDataString(id)}/result", null, cancellationToken, headersOnly: true);
        await using var file = File.Create(path);
        await response.Content.CopyToAsync(file, cancellationToken);
    }

    /// <summary>Cancels a running job or drops a finished one's files at once rather than at the service's next clean-up.</summary>
    public async Task DeleteJobAsync(string id)
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Delete, $"api/v1/jobs/{Uri.EscapeDataString(id)}", null, CancellationToken.None);
        }
        catch (VoiceServiceException)
        {
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, bool headersOnly = false)
    {
        var settings = options.Value;
        if (settings.BaseUri is not { } baseUri || settings.ResolvedApiKey is not { } key)
        {
            throw new VoiceServiceException("No voice service is configured (Voice:BaseUrl and its key).", HttpStatusCode.NotImplemented);
        }
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Add("X-Api-Key", key);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(
                request, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new VoiceServiceException($"The voice service cannot be reached: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceServiceException("The voice service did not answer in time.", HttpStatusCode.GatewayTimeout);
        }
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        using (response)
        {
            throw new VoiceServiceException(await ProblemAsync(response, "voice service", cancellationToken), Passed(response.StatusCode));
        }
    }

    /// <summary>The service's own title and detail; its messages are German, which is what the interface shows anyway.</summary>
    internal static async Task<string> ProblemAsync(HttpResponseMessage response, string service, CancellationToken cancellationToken)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) is { } problem)
            {
                var title = (string?)problem["title"];
                var detail = (string?)problem["detail"];
                if (title is not null || detail is not null)
                {
                    return title is not null && detail is not null ? $"{title}: {detail}" : (detail ?? title)!;
                }
            }
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
        }
        return $"The {service} answered {(int)response.StatusCode}.";
    }

    /// <summary>What the browser can act on keeps its status; a key or address problem here is the server's, a 502.</summary>
    internal static HttpStatusCode Passed(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable => status,
        _ => HttpStatusCode.BadGateway,
    };

    private static ReferenceVoice ToVoice(JsonObject voice) => new(
        (string?)voice["id"] ?? "",
        (string?)voice["label"] ?? "",
        (double?)voice["stored"]?["durationSeconds"] ?? 0,
        voice["createdAtUtc"] is JsonValue created && created.TryGetValue<DateTimeOffset>(out var at) ? at : null);

    private static VoiceJob ToJob(JsonObject job) => new(
        (string?)job["jobId"] ?? "",
        (string?)job["status"] ?? "QUEUED",
        (string?)job["error"]?["message"],
        job["startedAtUtc"] is JsonValue started && started.TryGetValue<DateTimeOffset>(out var at) ? at : null,
        (double?)job["estimatedDurationSeconds"] ?? 0);
}
