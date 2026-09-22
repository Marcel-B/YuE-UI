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
    /// <param name="model">
    /// Which separation model does the work, an id from <see cref="ListModelsAsync"/>; without one the
    /// service takes its own default.
    /// </param>
    Task<StemJob> StartAsync(Stream flacAudio, bool dereverb = false, string? model = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The models the service can separate with. The gateway answers this out of its own knowledge, so the
    /// list is there even while the Mac is not; a host shows it as the choice a job is started with.
    /// </summary>
    Task<IReadOnlyList<SeparationModel>> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<StemJob> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every job the service knows, newest first. The service takes only a couple of waiting jobs at a time,
    /// so this is how a host finds out what is holding the queue up and what can be removed.
    /// </summary>
    Task<IReadOnlyList<StemJob>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The result as a ZIP of WAV files; the caller owns the stream.</summary>
    Task<Stream> DownloadAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the import, whereupon the service removes the result and the job. The same call cancels a job
    /// that is still queued and frees its place; only a job being transferred to the Mac cannot be removed,
    /// which the service refuses with <see cref="HttpStatusCode.Conflict"/>.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <param name="Status">What the service reports: queued, processing, completed or failed.</param>
/// <param name="Attempts">How often the separation has been tried; it is retried after a network error.</param>
/// <param name="LastError">Why the job failed, if it did.</param>
/// <param name="CreatedUtc">When the recording was handed over; the list of jobs is sorted by it.</param>
/// <param name="UpdatedUtc">When the service last changed the job, e.g. the start of another attempt.</param>
/// <param name="Model">Which separation model the job runs with; the service names it back on every answer.</param>
public sealed record StemJob(
    Guid Id,
    string Status,
    int Attempts = 0,
    string? LastError = null,
    DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? UpdatedUtc = null,
    string? Model = null)
{
    // Read here rather than sent: a host that serializes the job passes on what the service said, no more.
    [JsonIgnore]
    public bool IsDone => StemJobStatus.Completed.Equals(Status, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFailed => StemJobStatus.Failed.Equals(Status, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One of the separation models the service offers. Which one is chosen decides both how long the job runs
/// and which stems come back, so a host shows more than the name: the id goes into the job, the rest is what
/// the choice is made by.
/// </summary>
/// <param name="Id">The identifier a job is started with.</param>
/// <param name="Name">How the service names the model for people.</param>
/// <param name="Family">The family it belongs to, e.g. several models of the same separator.</param>
/// <param name="Task">What it separates: vocals, instrumental, karaoke, 4stem, 6stem or drums.</param>
/// <param name="Stems">The files the result ZIP then holds, each without its .wav ending.</param>
/// <param name="Speed">How much it computes: fast, moderate, slow or verySlow.</param>
/// <param name="RealtimeFactor">
/// Audio length divided by computing time: 0.3 means a four-minute song takes about thirteen minutes.
/// </param>
/// <param name="Measured">False when the estimate comes from a comparable model rather than a measurement.</param>
/// <param name="Notes">What the service says about the model, if anything.</param>
/// <param name="IsDefault">The model a job without a choice runs with.</param>
public sealed record SeparationModel(
    string Id,
    string Name,
    string? Family = null,
    string? Task = null,
    IReadOnlyList<string>? Stems = null,
    string? Speed = null,
    double? RealtimeFactor = null,
    bool Measured = false,
    string? Notes = null,
    bool IsDefault = false);

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

    public async Task<StemJob> StartAsync(Stream flacAudio, bool dereverb = false, string? model = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flacAudio);
        using var content = new StreamContent(flacAudio);
        content.Headers.ContentType = new MediaTypeHeaderValue(AudioMediaType);

        // No model means the service's own default, so an empty choice is left out rather than sent empty.
        var url = $"api/jobs?dereverb={(dereverb ? "true" : "false")}";
        if (!string.IsNullOrWhiteSpace(model))
        {
            url += $"&model={Uri.EscapeDataString(model)}";
        }

        using var response = await SendAsync(() => client.PostAsync(url, content, cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SeparationModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.GetAsync("api/models", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var models = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.SeparationModelResponseArray, cancellationToken)
            .ConfigureAwait(false);
        return models is null
            ? throw new StemSeparationException("The stem service answered without a list of models.")
            : Array.ConvertAll(models, ToModel);
    }

    public async Task<StemJob> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.GetAsync($"api/jobs/{id}", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StemJob>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.GetAsync("api/jobs", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var jobs = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.StemJobResponseArray, cancellationToken)
            .ConfigureAwait(false);
        return jobs is null
            ? throw new StemSeparationException("The stem service answered without a list of jobs.")
            : Array.ConvertAll(jobs, ToJob);
    }

    public async Task<Stream> DownloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => client.GetAsync($"api/jobs/{id}/result", HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken).ConfigureAwait(false);
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
        using var response = await SendAsync(() => client.DeleteAsync($"api/jobs/{id}", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a request and turns a transport failure into the same kind of message as a refusal by the service.
    /// A stem job is optional, so a gateway that cannot be reached - wrong address, no route, no name - should
    /// leave the host with something to show rather than an unhandled error.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> request, CancellationToken cancellationToken)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new StemSeparationException($"The stem service cannot be reached: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StemSeparationException("The stem service did not answer in time.");
        }
    }

    private static async Task<StemJob> ReadJobAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var job = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.StemJobResponse, cancellationToken)
            .ConfigureAwait(false);
        return job is null
            ? throw new StemSeparationException("The stem service answered without a job.")
            : ToJob(job);
    }

    private static StemJob ToJob(StemJobResponse job) =>
        new(job.Id, job.Status ?? StemJobStatus.Queued, job.Attempts, job.LastError, job.CreatedUtc, job.UpdatedUtc, job.Model);

    private static SeparationModel ToModel(SeparationModelResponse model) =>
        new(
            model.Id,
            string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
            model.Family,
            model.Task,
            model.Stems ?? [],
            model.Speed,
            model.RealtimeFactor,
            model.Measured,
            model.Notes,
            model.IsDefault);

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
            HttpStatusCode.Conflict => "the job is being transferred to the Mac right now and cannot be removed until that is over",
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
public sealed record StemJobResponse(
    Guid Id,
    string? Status,
    int Attempts = 0,
    string? LastError = null,
    DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? UpdatedUtc = null,
    string? Model = null);

/// <summary>The model as the gateway writes it; mapped to <see cref="SeparationModel"/> right away.</summary>
public sealed record SeparationModelResponse(
    string Id,
    string? Name = null,
    string? Family = null,
    string? Task = null,
    string[]? Stems = null,
    string? Speed = null,
    double? RealtimeFactor = null,
    bool Measured = false,
    string? Notes = null,
    bool IsDefault = false);

public sealed record StemProblemDetails(string? Title, string? Detail);
