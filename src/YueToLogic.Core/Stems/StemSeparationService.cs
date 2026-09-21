using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using YueToLogic.Core.Serialization;

namespace YueToLogic.Core.Stems;

/// <summary>
/// Splits a YuE recording into stems through StemMyWav, which separates on a Mac and hands the work back
/// asynchronously: a job is started with the FLAC, polled until it is done, and its result fetched as a ZIP.
/// </summary>
/// <remarks>
/// The separation runs for minutes, which is why nothing here waits for it. The caller polls
/// <see cref="GetAsync"/> and, once the job reports <see cref="StemJobStatus.Completed"/>, downloads the
/// result and confirms it with <see cref="DeleteAsync"/> so that the service can drop the files.
/// </remarks>
public interface IStemSeparationService
{
    /// <summary>Hands the recording over; the job runs on by itself.</summary>
    /// <param name="dereverb">Also separates the reverb from the vocals, which takes longer.</param>
    Task<StemJob> StartAsync(Stream flacAudio, bool dereverb = false, CancellationToken cancellationToken = default);

    Task<StemJob> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The result as a ZIP of WAV files; the caller owns the stream.</summary>
    Task<Stream> DownloadAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Confirms the import, whereupon the service removes the result and the job.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <param name="Status">What the service reports: queued, processing, completed or failed.</param>
/// <param name="Attempts">How often the separation has been tried; it is retried after a network error.</param>
/// <param name="LastError">Why the job failed, if it did.</param>
public sealed record StemJob(Guid Id, string Status, int Attempts = 0, string? LastError = null)
{
    // Read here rather than sent: a host that serializes the job passes on what the service said, no more.
    [JsonIgnore]
    public bool IsDone => StemJobStatus.Completed.Equals(Status, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFailed => StemJobStatus.Failed.Equals(Status, StringComparison.OrdinalIgnoreCase);
}

public static class StemJobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>A request the stem service refused, with the reason it gave.</summary>
public sealed class StemSeparationException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Talks to a StemMyWav gateway over HTTP. The host supplies the <see cref="HttpClient"/> with the address and
/// the API key, so that the key stays in the host's configuration and never reaches a browser.
/// </summary>
public sealed class StemSeparationService(HttpClient client) : IStemSeparationService
{
    /// <summary>The gateway takes the file as the request body rather than as a form.</summary>
    private const string AudioMediaType = "audio/flac";

    public async Task<StemJob> StartAsync(Stream flacAudio, bool dereverb = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flacAudio);
        using var content = new StreamContent(flacAudio);
        content.Headers.ContentType = new MediaTypeHeaderValue(AudioMediaType);

        using var response = await client.PostAsync($"api/jobs?dereverb={(dereverb ? "true" : "false")}", content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StemJob> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync($"api/jobs/{id}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> DownloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"api/jobs/{id}/result", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await client.DeleteAsync($"api/jobs/{id}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StemJob> ReadJobAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var job = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.StemJobResponse, cancellationToken)
            .ConfigureAwait(false);
        return job is null
            ? throw new StemSeparationException("The stem service answered without a job.")
            : new StemJob(job.Id, job.Status ?? StemJobStatus.Queued, job.Attempts, job.LastError);
    }

    /// <summary>Turns a refusal into a message the host can show, since a stem job is optional anyway.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "the API key is not accepted",
            HttpStatusCode.NotFound => "the job is unknown; it may have been removed already",
            HttpStatusCode.RequestEntityTooLarge => "the recording is larger than the service accepts",
            HttpStatusCode.UnsupportedMediaType => "the file is not a FLAC",
            HttpStatusCode.TooManyRequests => "the service is busy; its queue is full",
            _ => await DetailAsync(response, cancellationToken).ConfigureAwait(false),
        };

        throw new StemSeparationException($"The stem service refused the request: {reason}.", response.StatusCode);
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync(YueToLogicJsonContext.Default.StemProblemDetails, cancellationToken)
                .ConfigureAwait(false);
            var detail = problem?.Detail ?? problem?.Title;
            return string.IsNullOrWhiteSpace(detail) ? $"HTTP {(int)response.StatusCode}" : detail;
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }
}

/// <summary>The job as the gateway writes it; mapped to <see cref="StemJob"/> right away.</summary>
public sealed record StemJobResponse(Guid Id, string? Status, int Attempts = 0, string? LastError = null);

public sealed record StemProblemDetails(string? Title, string? Detail);
