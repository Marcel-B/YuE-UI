using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using YueToLogic.Core.Serialization;

namespace YueToLogic.Core.Voices;

/// <summary>
/// Gives a sung recording the timbre of another voice through ChangeMyVoice: a reference voice is stored once
/// under a name, and a vocal track is then converted to it. Melody, phrasing and performance stay as they were.
/// </summary>
/// <remarks>
/// The conversion runs for minutes on the service's Mac, which is why nothing here waits for it: a job is
/// started, polled with <see cref="GetJobAsync"/> and, once it reports <see cref="VoiceJobStatus.Completed"/>,
/// fetched as a WAV. The reference voices are the collection the choice is made from; they stay until they
/// are deleted, while a job's result is kept only for a while.
/// </remarks>
public interface IVoiceConversionService
{
    /// <summary>Every reference voice the service keeps, the collection a job chooses from.</summary>
    Task<IReadOnlyList<ReferenceVoice>> ListVoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a recording as a reference voice under <paramref name="label"/>. The service accepts WAV, MP3,
    /// FLAC, M4A/AAC and OGG/Opus and keeps only the first 25 seconds, which is all the model uses.
    /// </summary>
    Task<ReferenceVoice> AddVoiceAsync(string label, Stream audio, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Removes a reference voice; the service refuses while a job still waits for it.</summary>
    Task DeleteVoiceAsync(string voiceId, CancellationToken cancellationToken = default);

    /// <summary>Hands the vocal track over; the conversion then runs on by itself.</summary>
    /// <param name="voiceId">The reference voice whose timbre the recording takes on.</param>
    Task<VoiceJob> StartJobAsync(
        Stream vocals,
        string fileName,
        string voiceId,
        VoiceConversionSettings? settings = null,
        CancellationToken cancellationToken = default);

    Task<VoiceJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A page of the service's jobs, newest first. The answer is paged because a job that has been cleared
    /// away stays on as a record, so the list only ever grows. Not every ChangeMyVoice answers this; one that
    /// does not refuses with <see cref="HttpStatusCode.NotFound"/>, which a host shows as "this service cannot
    /// list its jobs" rather than as a failure.
    /// </summary>
    /// <param name="status">Only jobs in this state: QUEUED, RUNNING, COMPLETED, FAILED or CANCELLED.</param>
    /// <param name="limit">How many jobs the page holds, 1 to 200; without one the service takes 50.</param>
    /// <param name="offset">How many jobs to skip; this is how the next page is asked for.</param>
    Task<VoiceJobPage> ListJobsAsync(
        string? status = null,
        int? limit = null,
        int? offset = null,
        CancellationToken cancellationToken = default);

    /// <summary>The converted recording as a WAV; the caller owns the stream.</summary>
    Task<Stream> DownloadResultAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a job and drops its files at once. The call is repeatable and is also how a finished job is
    /// confirmed, once its result has been taken into a project.
    /// </summary>
    Task DeleteJobAsync(string jobId, CancellationToken cancellationToken = default);
}

/// <summary>The properties of a recording as the service reads them.</summary>
/// <param name="Codec">The codec, e.g. <c>pcm_s16le</c> or <c>mp3</c>.</param>
public sealed record AudioProperties(string Codec, double DurationSeconds, int SampleRate, int Channels);

/// <summary>
/// A stored reference voice: what a conversion job is pointed at. Both the stored and the original properties
/// are kept, because the original says whether the recording was good enough for a convincing result.
/// </summary>
/// <param name="Stored">The file the service keeps — always mono PCM at 44.1 kHz.</param>
/// <param name="Original">The file as it was uploaded.</param>
public sealed record ReferenceVoice(
    string Id,
    string Label,
    DateTimeOffset? CreatedUtc = null,
    AudioProperties? Stored = null,
    AudioProperties? Original = null);

/// <param name="Status">What the service reports: QUEUED, RUNNING, COMPLETED, FAILED or CANCELLED.</param>
/// <param name="VoiceLabel">The reference voice's name when the job was accepted; it may be gone by now.</param>
/// <param name="ErrorCode">The service's own code for the failure, if the job failed.</param>
/// <param name="ResultSizeBytes">How large the converted recording is, once it is there.</param>
/// <param name="ResultSha256">
/// The checksum of the result, with which a transfer that broke off can be told from a complete one.
/// </param>
/// <param name="HasResult">
/// Whether the converted recording can still be fetched. The service keeps a record of every job it ever had,
/// so a job whose files it has cleared away - on request or after its retention - stays in the list with
/// everything but this; without it, a removal cannot be told from one that did nothing.
/// </param>
public sealed record VoiceJob(
    string Id,
    string Status,
    string? VoiceId = null,
    string? VoiceLabel = null,
    DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? FinishedUtc = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    long? ResultSizeBytes = null,
    string? ResultSha256 = null,
    bool HasResult = false)
{
    // Read here rather than sent: a host that serializes the job passes on what the service said, no more.
    [JsonIgnore]
    public bool IsDone => VoiceJobStatus.Completed.Equals(Status, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsFailed => VoiceJobStatus.Failed.Equals(Status, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One page of the job list, with what is needed to ask for the next: <paramref name="Total"/> counts every
/// job the filter matches, not the ones on this page.
/// </summary>
public sealed record VoiceJobPage(IReadOnlyList<VoiceJob> Jobs, int Total, int Limit, int Offset);

/// <summary>
/// What the model is told beyond the two recordings. Every value is optional; left out, the service takes its
/// own default, which is what a host does unless someone knows better.
/// </summary>
/// <param name="DiffusionSteps">How many steps the model computes: more is cleaner and slower.</param>
/// <param name="InferenceCfgRate">How closely it follows the reference voice.</param>
/// <param name="LengthAdjust">Stretches or compresses the result; 1.0 keeps the original length.</param>
/// <param name="F0Condition">Conditions on the sung pitch, which singing needs and speech does not.</param>
/// <param name="Fp16">Computes in half precision, which is faster on the service's hardware.</param>
public sealed record VoiceConversionSettings(
    int? DiffusionSteps = null,
    double? InferenceCfgRate = null,
    double? LengthAdjust = null,
    bool? F0Condition = null,
    bool? Fp16 = null);

public static class VoiceJobStatus
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>A request the voice service refused, with the reason it gave.</summary>
/// <remarks>
/// The service names its own reason in the <c>code</c> field of the problem document, which says more than
/// the status: a 409 is a name that is taken, a voice a job still waits for, or a result that is not there
/// yet. <see cref="VoiceConversionCode"/> holds the ones the service documents.
/// </remarks>
public sealed class VoiceConversionException(string message, HttpStatusCode? statusCode = null, string? code = null)
    : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>The service's own reason, e.g. <c>QUEUE_FULL</c>; null when it named none.</summary>
    public string? Code { get; } = code;

    /// <summary>
    /// Whether the same request is worth repeating later. A full queue, a rate limit, an absent Mac and a
    /// gateway with nothing behind it pass; a recording the service cannot use never will, however often it
    /// is sent.
    /// </summary>
    public bool CanRetryLater => StatusCode
        is HttpStatusCode.TooManyRequests
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.BadGateway
        or HttpStatusCode.GatewayTimeout;
}

/// <summary>The reasons ChangeMyVoice names in the <c>code</c> field of a problem document.</summary>
public static class VoiceConversionCode
{
    public const string InvalidAudio = "INVALID_AUDIO";
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    public const string ReferenceTooShort = "REFERENCE_TOO_SHORT";
    public const string DuplicateVoiceLabel = "DUPLICATE_VOICE_LABEL";
    public const string VoiceInUse = "VOICE_IN_USE";
    public const string ResultNotReady = "RESULT_NOT_READY";
    public const string ResultGone = "RESULT_GONE";
    public const string QueueFull = "QUEUE_FULL";
    public const string UpstreamUnavailable = "UPSTREAM_UNAVAILABLE";
}

/// <summary>
/// Talks to a ChangeMyVoice gateway over HTTP. The host supplies the <see cref="HttpClient"/> with the address
/// and the API key, so that the key stays in the host's configuration and never reaches a browser.
/// </summary>
public sealed class VoiceConversionService(HttpClient client) : IVoiceConversionService
{
    private const string Voices = "api/v1/voices";
    private const string Jobs = "api/v1/jobs";

    /// <summary>
    /// The service reads the format from the file itself, so what is claimed here hardly matters; a generic
    /// type keeps it from refusing a name it does not recognise.
    /// </summary>
    private const string AudioMediaType = "application/octet-stream";

    public async Task<IReadOnlyList<ReferenceVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.GetAsync(Voices, cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var voices = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.ReferenceVoiceResponseArray, cancellationToken)
            .ConfigureAwait(false);
        return voices is null
            ? throw new VoiceConversionException("The voice service answered without a list of voices.")
            : Array.ConvertAll(voices, ToVoice);
    }

    public async Task<ReferenceVoice> AddVoiceAsync(string label, Stream audio, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(audio);

        using var form = new MultipartFormDataContent { { new StringContent(label), "label" } };
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(AudioMediaType);
        form.Add(file, "file", FileName(fileName));

        using var response = await SendAsync(() => client.PostAsync(Voices, form, cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var voice = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.ReferenceVoiceResponse, cancellationToken)
            .ConfigureAwait(false);
        return voice is null
            ? throw new VoiceConversionException("The voice service answered without a voice.")
            : ToVoice(voice);
    }

    public async Task DeleteVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.DeleteAsync($"{Voices}/{Uri.EscapeDataString(voiceId)}", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceJob> StartJobAsync(
        Stream vocals,
        string fileName,
        string voiceId,
        VoiceConversionSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vocals);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);

        using var form = new MultipartFormDataContent { { new StringContent(voiceId), "voiceId" } };
        var source = new StreamContent(vocals);
        source.Headers.ContentType = new MediaTypeHeaderValue(AudioMediaType);
        form.Add(source, "source", FileName(fileName));

        // Every setting is optional; what is not chosen stays away so the service takes its own default.
        Add(form, "diffusionSteps", settings?.DiffusionSteps?.ToString(CultureInfo.InvariantCulture));
        Add(form, "inferenceCfgRate", settings?.InferenceCfgRate?.ToString(CultureInfo.InvariantCulture));
        Add(form, "lengthAdjust", settings?.LengthAdjust?.ToString(CultureInfo.InvariantCulture));
        Add(form, "f0Condition", settings?.F0Condition?.ToString().ToLowerInvariant());
        Add(form, "fp16", settings?.Fp16?.ToString().ToLowerInvariant());

        using var response = await SendAsync(() => client.PostAsync(Jobs, form, cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.GetAsync($"{Jobs}/{Uri.EscapeDataString(jobId)}", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJobAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VoiceJobPage> ListJobsAsync(
        string? status = null,
        int? limit = null,
        int? offset = null,
        CancellationToken cancellationToken = default)
    {
        // What is not asked for stays out of the query, so the service's own defaults apply.
        var query = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (limit is { } count)
        {
            query.Add($"limit={count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (offset is { } skip)
        {
            query.Add($"offset={skip.ToString(CultureInfo.InvariantCulture)}");
        }

        var url = query.Count == 0 ? Jobs : $"{Jobs}?{string.Join('&', query)}";
        using var response = await SendAsync(() => client.GetAsync(url, cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var page = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.VoiceJobListResponse, cancellationToken)
            .ConfigureAwait(false);
        return page is null
            ? throw new VoiceConversionException("The voice service answered without a list of jobs.")
            : new VoiceJobPage(Array.ConvertAll(page.Items ?? [], ToJob), page.Total, page.Limit, page.Offset);
    }

    public async Task<Stream> DownloadResultAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            () => client.GetAsync($"{Jobs}/{Uri.EscapeDataString(jobId)}/result", HttpCompletionOption.ResponseHeadersRead, cancellationToken),
            cancellationToken).ConfigureAwait(false);
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

    public async Task DeleteJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => client.DeleteAsync($"{Jobs}/{Uri.EscapeDataString(jobId)}", cancellationToken), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static void Add(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            form.Add(new StringContent(value), name);
        }
    }

    /// <summary>The service reads the extension, so a name it can work with is sent even when none is known.</summary>
    private static string FileName(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? "audio.wav" : Path.GetFileName(fileName);

    /// <summary>
    /// Runs a request and turns a transport failure into the same kind of message as a refusal by the service.
    /// Changing the voice is optional, so a gateway that cannot be reached - wrong address, no route, no name -
    /// should leave the host with something to show rather than an unhandled error.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> request, CancellationToken cancellationToken)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new VoiceConversionException($"The voice service cannot be reached: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceConversionException("The voice service did not answer in time.");
        }
    }

    private static async Task<VoiceJob> ReadJobAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var job = await response.Content
            .ReadFromJsonAsync(YueToLogicJsonContext.Default.VoiceJobResponse, cancellationToken)
            .ConfigureAwait(false);
        return job is null
            ? throw new VoiceConversionException("The voice service answered without a job.")
            : ToJob(job);
    }

    private static VoiceJob ToJob(VoiceJobResponse job) =>
        new(
            job.JobId,
            job.Status ?? VoiceJobStatus.Queued,
            job.VoiceId,
            job.VoiceLabel,
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.FinishedAtUtc,
            job.Error?.Code,
            job.Error?.Message,
            job.ResultSizeBytes,
            job.ResultSha256,
            // The service names where the result can be fetched only while it is there.
            !string.IsNullOrWhiteSpace(job.ResultUrl));

    private static ReferenceVoice ToVoice(ReferenceVoiceResponse voice) =>
        new(
            voice.Id,
            string.IsNullOrWhiteSpace(voice.Label) ? voice.Id : voice.Label,
            voice.CreatedAtUtc,
            ToProperties(voice.Stored),
            ToProperties(voice.Original));

    private static AudioProperties? ToProperties(AudioPropertiesResponse? properties) =>
        properties is null ? null : new AudioProperties(properties.Codec ?? string.Empty, properties.DurationSeconds, properties.SampleRate, properties.Channels);

    /// <summary>
    /// Turns a refusal into a message the host can show, since changing the voice is optional anyway. The
    /// service's own <c>code</c> is read first: it tells apart what the status alone leaves open.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The gateway's own failure, not the service's: nothing there read the request, so this reads like a
        // service that cannot be reached rather than like one that refused something.
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
        {
            throw new VoiceConversionException(
                $"The voice service cannot be reached: its gateway answered HTTP {(int)response.StatusCode}, so the service behind it is probably not running.",
                response.StatusCode);
        }

        var problem = await ProblemAsync(response, cancellationToken).ConfigureAwait(false);
        var reason = problem?.Code switch
        {
            VoiceConversionCode.InvalidAudio => "the recording cannot be read as audio",
            VoiceConversionCode.UnsupportedFormat => "the file is in a format it does not take; WAV, MP3, FLAC, M4A/AAC and OGG/Opus work",
            VoiceConversionCode.ReferenceTooShort => "the recording of the voice is too short to model a timbre from",
            VoiceConversionCode.DuplicateVoiceLabel => "a voice of that name is already there",
            VoiceConversionCode.VoiceInUse => "a job still waits for that voice, so it cannot be removed yet",
            VoiceConversionCode.ResultNotReady => "the result is not there yet",
            VoiceConversionCode.ResultGone => "the result has been cleared away; the job would have to run again",
            VoiceConversionCode.QueueFull => "the service is busy; its queue is full",
            VoiceConversionCode.UpstreamUnavailable => "the Mac that converts cannot be reached right now",
            _ => response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "the API key is not accepted",
                HttpStatusCode.Forbidden => "this server is not among the addresses it answers",
                HttpStatusCode.NotFound => "it knows neither that job nor that voice; it may have been removed already",
                HttpStatusCode.TooManyRequests => "too many requests; this key's rate limit is reached",
                HttpStatusCode.RequestEntityTooLarge => "the recording is larger than the service accepts",
                // The service names a code for all of these; without one, its status is all there is to go on.
                HttpStatusCode.Conflict => "the request does not fit what it has right now",
                HttpStatusCode.Gone => "the result has been cleared away; the job would have to run again",
                HttpStatusCode.ServiceUnavailable => "the service is not available right now",
                _ => Detail(problem, response.StatusCode),
            },
        };

        throw new VoiceConversionException($"The voice service refused the request: {reason}.", response.StatusCode, problem?.Code);
    }

    private static async Task<VoiceProblemDetails?> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync(YueToLogicJsonContext.Default.VoiceProblemDetails, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            // An answer that is not a problem document says nothing beyond its status, which is used below.
            return null;
        }
    }

    private static string Detail(VoiceProblemDetails? problem, HttpStatusCode status)
    {
        var detail = problem?.Detail ?? problem?.Title;
        return string.IsNullOrWhiteSpace(detail) ? $"HTTP {(int)status}" : detail;
    }
}

/// <summary>A page of jobs as the service writes it; mapped to <see cref="VoiceJobPage"/> right away.</summary>
public sealed record VoiceJobListResponse(
    VoiceJobResponse[]? Items = null,
    int Total = 0,
    int Limit = 0,
    int Offset = 0);

/// <summary>The job as the service writes it; mapped to <see cref="VoiceJob"/> right away.</summary>
public sealed record VoiceJobResponse(
    string JobId,
    string? Status = null,
    string? VoiceId = null,
    string? VoiceLabel = null,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? FinishedAtUtc = null,
    VoiceJobErrorResponse? Error = null,
    string? ResultUrl = null,
    long? ResultSizeBytes = null,
    string? ResultSha256 = null);

public sealed record VoiceJobErrorResponse(string? Code = null, string? Message = null);

/// <summary>The voice as the service writes it; mapped to <see cref="ReferenceVoice"/> right away.</summary>
public sealed record ReferenceVoiceResponse(
    string Id,
    string? Label = null,
    DateTimeOffset? CreatedAtUtc = null,
    AudioPropertiesResponse? Stored = null,
    AudioPropertiesResponse? Original = null);

public sealed record AudioPropertiesResponse(
    string? Codec = null,
    double DurationSeconds = 0,
    int SampleRate = 0,
    int Channels = 0);

/// <param name="Code">The service's own reason, e.g. <c>QUEUE_FULL</c>; see <see cref="VoiceConversionCode"/>.</param>
public sealed record VoiceProblemDetails(string? Title = null, string? Detail = null, string? Code = null);
