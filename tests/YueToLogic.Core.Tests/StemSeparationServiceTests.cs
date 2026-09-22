using System.Net;
using System.Text;
using YueToLogic.Core.Stems;

namespace YueToLogic.Core.Tests;

public class StemSeparationServiceTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Starting_a_job_sends_the_recording_as_the_body()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, $$"""{"id":"{{JobId}}","status":"queued"}""");

        var job = await Service(handler).StartAsync(new MemoryStream("fLaC"u8.ToArray()), dereverb: true);

        Assert.Equal(new StemJob(JobId, "queued"), job);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        // No model chosen: the parameter stays away so that the service takes its own default.
        Assert.Equal("/api/jobs?dereverb=true", handler.Request.RequestUri!.PathAndQuery);
        Assert.Equal("audio/flac", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("fLaC", handler.Body);
        Assert.Equal("secret", Assert.Single(handler.Request.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task The_chosen_model_travels_with_the_job_and_comes_back_with_it()
    {
        var handler = new StubHandler(HttpStatusCode.Accepted, $$"""{"id":"{{JobId}}","status":"queued","model":"mel_roformer"}""");

        var job = await Service(handler).StartAsync(new MemoryStream("fLaC"u8.ToArray()), dereverb: false, model: "mel_roformer");

        Assert.Equal("/api/jobs?dereverb=false&model=mel_roformer", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal("mel_roformer", job.Model);
    }

    [Fact]
    public async Task The_models_of_the_service_are_read_with_what_the_choice_is_made_by()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """[{"id":"htdemucs","name":"HTDemucs","family":"demucs","task":"4stem","stems":["vocals","drums","bass","other"],"speed":"fast","realtimeFactor":2.5,"measured":true,"notes":null,"isDefault":true},{"id":"mel_roformer","name":"Mel-RoFormer","family":"roformer","task":"vocals","stems":["vocals","instrumental"],"speed":"slow","realtimeFactor":0.3,"measured":false,"notes":"the cleanest vocals","isDefault":false}]""");

        var models = await Service(handler).ListModelsAsync();

        Assert.Equal("/api/models", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal(2, models.Count);
        Assert.Equal(("htdemucs", "HTDemucs", "4stem"), (models[0].Id, models[0].Name, models[0].Task));
        Assert.Equal(["vocals", "drums", "bass", "other"], models[0].Stems!);
        Assert.Equal((2.5, true, true), (models[0].RealtimeFactor, models[0].Measured, models[0].IsDefault));
        Assert.Equal(("slow", "the cleanest vocals", false), (models[1].Speed, models[1].Notes, models[1].IsDefault));
    }

    [Fact]
    public async Task A_model_without_a_name_is_known_by_its_id()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """[{"id":"htdemucs"}]""");

        var model = Assert.Single(await Service(handler).ListModelsAsync());

        Assert.Equal(("htdemucs", "htdemucs"), (model.Id, model.Name));
        Assert.Empty(model.Stems!);
    }

    [Fact]
    public async Task A_finished_job_is_recognized_and_a_failed_one_carries_its_reason()
    {
        var done = await Service(new StubHandler(HttpStatusCode.OK, $$"""{"id":"{{JobId}}","status":"completed","attempts":1,"lastError":null}""")).GetAsync(JobId);
        var failed = await Service(new StubHandler(HttpStatusCode.OK, $$"""{"id":"{{JobId}}","status":"failed","attempts":3,"lastError":"the Mac said no"}""")).GetAsync(JobId);

        Assert.True(done.IsDone);
        Assert.False(done.IsFailed);
        Assert.True(failed.IsFailed);
        Assert.Equal((3, "the Mac said no"), (failed.Attempts, failed.LastError));
    }

    [Fact]
    public async Task The_list_of_jobs_comes_with_their_timestamps()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            $$"""[{"id":"{{JobId}}","status":"processing","attempts":2,"lastError":null,"createdUtc":"2026-09-22T08:00:00+00:00","updatedUtc":"2026-09-22T08:05:00+00:00"},{"id":"22222222-2222-3333-4444-555555555555","status":"queued","attempts":0,"lastError":null,"createdUtc":"2026-09-22T07:00:00+00:00","updatedUtc":"2026-09-22T07:00:00+00:00"}]""");

        var jobs = await Service(handler).ListAsync();

        Assert.Equal("/api/jobs", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(JobId, jobs[0].Id);
        Assert.Equal((StemJobStatus.Processing, 2), (jobs[0].Status, jobs[0].Attempts));
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero), jobs[0].CreatedUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 5, 0, TimeSpan.Zero), jobs[0].UpdatedUtc);
        Assert.Equal(StemJobStatus.Queued, jobs[1].Status);
    }

    [Fact]
    public async Task The_result_is_handed_on_as_a_stream()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "PKstems");

        await using var result = await Service(handler).DownloadAsync(JobId);

        Assert.Equal("PKstems", await new StreamReader(result).ReadToEndAsync());
        Assert.Equal($"/api/jobs/{JobId}/result", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Confirming_the_import_deletes_the_job()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent, string.Empty);

        await Service(handler).DeleteAsync(JobId);

        Assert.Equal(HttpMethod.Delete, handler.Request!.Method);
        Assert.Equal($"/api/jobs/{JobId}", handler.Request.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "queue is full")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "larger than the service accepts")]
    [InlineData(HttpStatusCode.Conflict, "being transferred")]
    public async Task A_refused_request_says_what_the_service_answered(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(status, string.Empty);

        var exception = await Assert.ThrowsAsync<StemSeparationException>(() => Service(handler).GetAsync(JobId));

        Assert.Equal(status, exception.StatusCode);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_error_falls_back_to_what_the_answer_said()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, """{"title":"Separator down","detail":"the Mac is not answering"}""");

        var exception = await Assert.ThrowsAsync<StemSeparationException>(() => Service(handler).GetAsync(JobId));

        Assert.Contains("the Mac is not answering", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_that_cannot_be_reached_says_so_instead_of_throwing_something_else()
    {
        var handler = new StubHandler(HttpStatusCode.OK, string.Empty) { Transport = new HttpRequestException("No such host is known") };

        var exception = await Assert.ThrowsAsync<StemSeparationException>(() => Service(handler).GetAsync(JobId));

        Assert.Contains("cannot be reached", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No such host is known", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task A_service_that_does_not_answer_in_time_says_so()
    {
        var handler = new StubHandler(HttpStatusCode.OK, string.Empty) { Transport = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout") };

        var exception = await Assert.ThrowsAsync<StemSeparationException>(() => Service(handler).GetAsync(JobId));

        Assert.Contains("did not answer in time", exception.Message, StringComparison.Ordinal);
    }

    private static StemSeparationService Service(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://stems.example/") };
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        return new StemSeparationService(client);
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
