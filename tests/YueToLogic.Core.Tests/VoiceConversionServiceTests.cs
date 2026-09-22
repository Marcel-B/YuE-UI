using System.Net;
using System.Text;
using YueToLogic.Core.Voices;

namespace YueToLogic.Core.Tests;

public class VoiceConversionServiceTests
{
    private const string JobId = "job-4711";

    [Fact]
    public async Task The_collection_is_read_with_what_a_recording_is_made_of()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """
            [{"id":"v1","label":"Marcel","createdAtUtc":"2026-09-20T10:00:00+00:00",
              "stored":{"codec":"pcm_s16le","durationSeconds":25,"sampleRate":44100,"channels":1},
              "original":{"codec":"mp3","durationSeconds":41.5,"sampleRate":48000,"channels":2}}]
            """);

        var voice = Assert.Single(await Service(handler).ListVoicesAsync());

        Assert.Equal("/api/v1/voices", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal(("v1", "Marcel"), (voice.Id, voice.Label));
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), voice.CreatedUtc);
        Assert.Equal(new AudioProperties("pcm_s16le", 25, 44100, 1), voice.Stored);
        Assert.Equal(new AudioProperties("mp3", 41.5, 48000, 2), voice.Original);
        Assert.Equal("secret", Assert.Single(handler.Request.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task A_voice_without_a_label_is_known_by_its_id()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """[{"id":"v1"}]""");

        var voice = Assert.Single(await Service(handler).ListVoicesAsync());

        Assert.Equal(("v1", "v1"), (voice.Id, voice.Label));
        Assert.Null(voice.Stored);
    }

    [Fact]
    public async Task A_new_voice_is_sent_as_a_form_of_name_and_recording()
    {
        var handler = new StubHandler(HttpStatusCode.Created, """{"id":"v2","label":"Sängerin"}""");

        var voice = await Service(handler).AddVoiceAsync("Sängerin", new MemoryStream("RIFF"u8.ToArray()), "voice.wav");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/v1/voices", handler.Request.RequestUri!.PathAndQuery);
        Assert.StartsWith("multipart/form-data", handler.Request.Content!.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        Assert.Contains("name=label", handler.Body, StringComparison.Ordinal);
        Assert.Contains("Sängerin", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("filename=voice.wav", handler.Body, StringComparison.Ordinal);
        Assert.Equal("v2", voice.Id);
    }

    [Fact]
    public async Task Starting_a_job_names_the_voice_and_sends_the_vocals()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, $$"""{"jobId":"{{JobId}}","status":"QUEUED","voiceId":"v1","voiceLabel":"Marcel"}""");

        var job = await Service(handler).StartJobAsync(new MemoryStream("RIFF"u8.ToArray()), "vocals.wav", "v1");

        Assert.Equal("/api/v1/jobs", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Contains("name=voiceId", handler.Body, StringComparison.Ordinal);
        Assert.Contains("filename=vocals.wav", handler.Body!, StringComparison.Ordinal);
        // Nothing was chosen, so the model's own defaults stay away from the form.
        Assert.DoesNotContain("diffusionSteps", handler.Body, StringComparison.Ordinal);
        Assert.Equal((JobId, VoiceJobStatus.Queued, "Marcel"), (job.Id, job.Status, job.VoiceLabel));
    }

    [Fact]
    public async Task What_the_model_is_told_travels_with_the_job_when_it_is_chosen()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, $$"""{"jobId":"{{JobId}}","status":"QUEUED"}""");

        await Service(handler).StartJobAsync(
            new MemoryStream("RIFF"u8.ToArray()),
            "vocals.wav",
            "v1",
            new VoiceConversionSettings(DiffusionSteps: 50, InferenceCfgRate: 0.7, LengthAdjust: 1, F0Condition: true, Fp16: false));

        Assert.Contains("50", handler.Body, StringComparison.Ordinal);
        // The numbers are written the way the service reads them, whatever the culture of the host.
        Assert.Contains("0.7", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("true", handler.Body, StringComparison.Ordinal);
        Assert.Contains("false", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_job_is_recognized_and_a_failed_one_carries_its_reason()
    {
        var done = await Service(new StubHandler(
            HttpStatusCode.OK,
            $$"""{"jobId":"{{JobId}}","status":"COMPLETED","resultSizeBytes":8372364,"finishedAtUtc":"2026-09-22T09:00:00+00:00"}""")).GetJobAsync(JobId);
        var failed = await Service(new StubHandler(
            HttpStatusCode.OK,
            $$$"""{"jobId":"{{{JobId}}}","status":"FAILED","error":{"code":"MODEL_ERROR","message":"the Mac said no"}}""")).GetJobAsync(JobId);

        Assert.True(done.IsDone);
        Assert.False(done.IsFailed);
        Assert.Equal(8372364, done.ResultSizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero), done.FinishedUtc);
        Assert.True(failed.IsFailed);
        Assert.Equal(("MODEL_ERROR", "the Mac said no"), (failed.ErrorCode, failed.ErrorMessage));
    }

    [Fact]
    public async Task The_list_of_jobs_comes_with_their_timestamps()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            $$"""
            [{"jobId":"{{JobId}}","status":"RUNNING","voiceLabel":"Marcel","createdAtUtc":"2026-09-22T08:00:00+00:00","startedAtUtc":"2026-09-22T08:01:00+00:00"},
             {"jobId":"job-0815","status":"CANCELLED","createdAtUtc":"2026-09-22T07:00:00+00:00"}]
            """);

        var jobs = await Service(handler).ListJobsAsync();

        Assert.Equal("/api/v1/jobs", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal(2, jobs.Count);
        Assert.Equal((JobId, VoiceJobStatus.Running, "Marcel"), (jobs[0].Id, jobs[0].Status, jobs[0].VoiceLabel));
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 1, 0, TimeSpan.Zero), jobs[0].StartedUtc);
        Assert.Equal(VoiceJobStatus.Cancelled, jobs[1].Status);
    }

    /// <summary>
    /// A service that has no route for all of its jobs answers 404. The host tells that apart from a job that
    /// is gone by where it asked, so the status has to survive the way up.
    /// </summary>
    [Fact]
    public async Task A_service_that_cannot_list_its_jobs_says_so_with_a_not_found()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, string.Empty);

        var exception = await Assert.ThrowsAsync<VoiceConversionException>(() => Service(handler).ListJobsAsync());

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task The_result_is_handed_on_as_a_stream()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "RIFFvocals");

        await using var result = await Service(handler).DownloadResultAsync(JobId);

        Assert.Equal("RIFFvocals", await new StreamReader(result).ReadToEndAsync());
        Assert.Equal($"/api/v1/jobs/{JobId}/result", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Confirming_the_import_deletes_the_job_and_a_voice_is_deleted_by_its_id()
    {
        var job = new StubHandler(HttpStatusCode.NoContent, string.Empty);
        var voice = new StubHandler(HttpStatusCode.NoContent, string.Empty);

        await Service(job).DeleteJobAsync(JobId);
        await Service(voice).DeleteVoiceAsync("v 1");

        Assert.Equal(HttpMethod.Delete, job.Request!.Method);
        Assert.Equal($"/api/v1/jobs/{JobId}", job.Request.RequestUri!.PathAndQuery);
        // An id with something in it that a URL cannot hold plainly is escaped rather than sent as it is.
        Assert.Equal("/api/v1/voices/v%201", voice.Request!.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.Forbidden, "addresses it answers")]
    [InlineData(HttpStatusCode.Conflict, "still waits for")]
    [InlineData(HttpStatusCode.Gone, "cleared away")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "larger than the service accepts")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "queue is full")]
    public async Task A_refused_request_says_what_the_service_answered(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(status, string.Empty);

        var exception = await Assert.ThrowsAsync<VoiceConversionException>(() => Service(handler).GetJobAsync(JobId));

        Assert.Equal(status, exception.StatusCode);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_error_falls_back_to_what_the_answer_said()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, """{"title":"Model down","detail":"the model did not load"}""");

        var exception = await Assert.ThrowsAsync<VoiceConversionException>(() => Service(handler).GetJobAsync(JobId));

        Assert.Contains("the model did not load", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_says_so_instead_of_throwing_something_else()
    {
        var handler = new StubHandler(HttpStatusCode.OK, string.Empty) { Transport = new HttpRequestException("No such host is known") };

        var exception = await Assert.ThrowsAsync<VoiceConversionException>(() => Service(handler).ListVoicesAsync());

        Assert.Contains("cannot be reached", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task A_service_that_does_not_answer_in_time_says_so()
    {
        var handler = new StubHandler(HttpStatusCode.OK, string.Empty)
        {
            Transport = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        };

        var exception = await Assert.ThrowsAsync<VoiceConversionException>(() => Service(handler).GetJobAsync(JobId));

        Assert.Contains("did not answer in time", exception.Message, StringComparison.Ordinal);
    }

    private static VoiceConversionService Service(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://voice.example/") };
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        return new VoiceConversionService(client);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        /// <summary>Thrown instead of answering, the way a broken connection or a timeout does.</summary>
        public Exception? Transport { get; init; }

        /// <summary>The body that was sent, read before the request is disposed.</summary>
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (Transport is not null)
            {
                throw Transport;
            }

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, body.StartsWith('{') || body.StartsWith('[') ? "application/json" : "application/octet-stream"),
            };
        }
    }
}
