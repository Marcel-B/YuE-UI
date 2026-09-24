using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Library;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class TranscriptionEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public TranscriptionEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task Without_SheetSage2_a_recording_is_refused_and_no_worker_starts()
    {
        var response = await Upload("song.mp3");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, _app.Launcher.Launches);
        Assert.False((await _client.GetFromJsonAsync<TranscriptionList>("/api/transcriptions", TestApp.Json))!.Installed);
    }

    [Fact]
    public async Task A_request_without_a_recording_or_with_an_unknown_task_is_refused()
    {
        _app.InstallSheetSage();
        using var form = new MultipartFormDataContent { { new StringContent("melody-chords"), "task" } };

        var response = await _client.PostAsync("/api/transcriptions", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["file", "task"], errors.Select(e => e.Key));
    }

    [Fact]
    public async Task An_upload_is_handed_to_the_worker_under_a_safe_name()
    {
        _app.InstallSheetSage();

        var response = await Upload("../My Song!.mp3", "melody-vocal");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var state = (await response.Content.ReadFromJsonAsync<TranscriptionState>(TestApp.Json))!;
        var command = await _app.Worker.NextCommand();
        Assert.Equal("transcribe", (string?)command["cmd"]);
        Assert.Equal(state.Id, (string?)command["id"]);
        Assert.Equal("melody-vocal", (string?)command["task"]);
        // Its models are not cached here, so SheetSage2 may download them.
        Assert.False((bool?)command["offline"]);
        var audio = (string)command["audio"]!;
        Assert.Equal("My Song.mp3", Path.GetFileName(audio));
        Assert.Equal("ID3 audio", await File.ReadAllTextAsync(audio));
        Assert.NotEqual(default, state.UpdatedAt);
    }

    [Fact]
    public async Task Worker_events_carry_the_transcription_to_its_score_and_the_upload_is_removed()
    {
        _app.InstallSheetSage();
        var (id, audio) = await StartTranscription();

        _app.Worker.Emit(new { @event = "transcribe", id, stage = "progress", fraction = 0.4, detail = "transcribing · 0:12" });
        await _app.WaitForStatus(_client, s => s.Transcriptions!.Any(t => t.Fraction == 0.4));
        // A heartbeat without a fraction keeps the last one.
        _app.Worker.Emit(new { @event = "transcribe", id, stage = "progress", detail = "transcribing · 0:17 elapsed" });
        var beating = await _app.WaitForStatus(_client, s => s.Transcriptions!.Any(t => t.Detail.EndsWith("elapsed", StringComparison.Ordinal)));
        Assert.Equal(0.4, Assert.Single(beating.Transcriptions!).Fraction);

        var output = _app.AddTranscription("My-Song-20260923-201500");
        _app.Worker.Emit(new { @event = "transcribe", id, stage = "done", abc = "X:1\nV:Vocal\n", warnings = new[] { "key uncertain" }, output });
        var status = await _app.WaitForStatus(_client, s => s.Transcriptions!.Any(t => t.Finished));

        var done = Assert.Single(status.Transcriptions!);
        Assert.Equal("done", done.Stage);
        Assert.Equal(1, done.Fraction);
        Assert.Equal("X:1\nV:Vocal\n", done.Abc);
        Assert.Equal(["key uncertain"], done.Warnings);
        Assert.Equal("My-Song-20260923-201500", done.Result);
        Assert.False(Directory.Exists(Path.GetDirectoryName(audio)));
    }

    [Fact]
    public async Task A_failed_transcription_says_why()
    {
        _app.InstallSheetSage();
        var (id, _) = await StartTranscription();

        _app.Worker.Emit(new { @event = "transcribe", id, stage = "failed", code = "afconvert", message = "unsupported file" });
        // The state goes out just before the log entry; wait for both.
        var status = await _app.WaitForStatus(_client, s => s.Transcriptions!.Any(t => t.Finished)
            && s.Log.Any(entry => entry.Level == "error" && entry.Message.Contains("unsupported file", StringComparison.Ordinal)));

        var failed = Assert.Single(status.Transcriptions!);
        Assert.Equal("afconvert", failed.Code);
        Assert.Equal("unsupported file", failed.Message);
    }

    [Fact]
    public async Task Only_one_transcription_runs_at_a_time()
    {
        _app.InstallSheetSage();
        await StartTranscription();

        var response = await Upload("second.wav");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_asks_the_worker_to_stop_the_running_transcription()
    {
        _app.InstallSheetSage();
        var (id, _) = await StartTranscription();

        var response = await _client.PostAsync($"/api/transcriptions/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("transcribe_cancel", (string?)(await _app.Worker.NextCommand())["cmd"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/transcriptions/nope/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task When_the_worker_dies_a_running_transcription_fails()
    {
        _app.InstallSheetSage();
        await StartTranscription();

        _app.Worker.Exit();
        var status = await _app.WaitForStatus(_client, s => s.Transcriptions!.Any(t => t.Finished));

        Assert.Equal("failed", Assert.Single(status.Transcriptions!).Stage);
    }

    [Fact]
    public async Task Finished_transcriptions_are_listed_newest_first_without_failed_ones()
    {
        _app.InstallSheetSage();
        _app.AddTranscription("Old-20260901-080000", source: "Old.wav");
        _app.AddTranscription("My-Song-20260923-201500");
        _app.AddTranscription("Broken-20260923-210000", score: null);

        var list = (await _client.GetFromJsonAsync<TranscriptionList>("/api/transcriptions", TestApp.Json))!;

        Assert.True(list.Installed);
        Assert.Equal(["My-Song-20260923-201500", "Old-20260901-080000"], list.Items.Select(t => t.Id));
        var latest = list.Items[0];
        Assert.Equal("My Song.mp3", latest.SourceName);
        Assert.Equal("melody-vocal", latest.Task);
        Assert.Equal(["key uncertain"], latest.Warnings);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 23, 20, 15, 0, DateTimeKind.Local)), latest.CreatedAt);
    }

    [Fact]
    public async Task The_score_and_all_files_of_a_transcription_can_be_downloaded()
    {
        _app.AddTranscription("My-Song-20260923-201500");

        var score = await _client.GetAsync("/api/transcriptions/My-Song-20260923-201500/score?download=true");
        Assert.Equal(HttpStatusCode.OK, score.StatusCode);
        Assert.Equal("X:1\nV:Vocal\nE2G2|\n", await score.Content.ReadAsStringAsync());
        Assert.Equal("My-Song-20260923-201500.abc", score.Content.Headers.ContentDisposition?.FileNameStar);

        var zip = await _client.GetAsync("/api/transcriptions/My-Song-20260923-201500/zip");
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        using var archive = new ZipArchive(await zip.Content.ReadAsStreamAsync());
        Assert.Contains("score.abc", archive.Entries.Select(e => e.FullName));
        Assert.Contains("transcription.mid", archive.Entries.Select(e => e.FullName));
    }

    [Theory]
    [InlineData("Unknown-20260923-201500")]
    [InlineData("..")]
    [InlineData("..%2Fsongs")]
    public async Task Only_folders_in_the_transcription_directory_are_served(string id)
    {
        _app.AddTranscription("My-Song-20260923-201500");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/transcriptions/{id}/score")).StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private async Task<HttpResponseMessage> Upload(string fileName, string task = "melody-full")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("ID3 audio"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(task), "task");
        return await _client.PostAsync("/api/transcriptions", form);
    }

    private async Task<(string Id, string Audio)> StartTranscription()
    {
        var response = await Upload("My Song.mp3");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        return ((string)command["id"]!, (string)command["audio"]!);
    }
}
