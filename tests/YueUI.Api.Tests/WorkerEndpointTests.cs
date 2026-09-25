using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class WorkerEndpointTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public WorkerEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task Generate_starts_the_worker_and_sends_the_command()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = " Dark synthwave ",
            lyrics = "[verse]\nNeon lights",
            title = "Neon Night",
            batch = 2,
            quality = "full",
            draftSteps = 12,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("generate", (string?)command["cmd"]);
        Assert.Equal("Dark synthwave", (string?)command["style"]);
        Assert.Equal("Neon Night", (string?)command["title"]);
        Assert.Equal(2, (int?)command["batch"]);
        Assert.Equal("full", (string?)command["quality"]);
        Assert.Equal(12, (int?)command["draft_steps"]);
        // No seed given: the worker picks one.
        Assert.True((bool?)command["random_seed"]);
        Assert.Null(command["engines"]);
    }

    [Fact]
    public async Task A_score_and_a_length_limit_reach_the_worker()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = "English, piano pop",
            lyrics = "[verse]\nNeon lights",
            cot = "melody",
            maxTokens = 3000,
            abc = "\nX:1\nK:C\nE2G2A2G2|\n",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("melody", (string?)command["cot"]);
        Assert.Equal(3000, (int?)command["max_tokens"]);
        Assert.Equal("X:1\nK:C\nE2G2A2G2|", (string?)command["abc"]);
    }

    [Fact]
    public async Task Defaults_leave_length_and_score_to_the_worker()
    {
        var command = await Generate();

        Assert.Null(command["max_tokens"]);
        Assert.Null(command["abc"]);
    }

    [Fact]
    public async Task An_instrumental_needs_no_lyrics()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Ambient", lyrics = "", instrumental = true });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", (string?)(await _app.Worker.NextCommand())["lyrics"]);
    }

    [Fact]
    public async Task A_score_without_planning_and_an_impossible_length_are_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "x", cot = "off", abc = "X:1", maxTokens = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["maxTokens", "abc"], errors.Select(e => e.Key));
    }

    [Fact]
    public async Task Sampling_full_steps_and_long_songs_reach_the_worker_extension()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = "English, piano pop",
            lyrics = "[verse]\nNeon lights",
            quality = "full",
            fullSteps = 48,
            maxTokens = 15000,
            abcSampling = new { temperature = 0.6, topK = 20 },
            semanticSampling = new { topP = 0.9, repetitionPenalty = 1.1, penaltyWindow = 40 },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal(48, (int?)command["full_steps"]);
        Assert.Equal(15000, (int?)command["max_tokens"]);
        Assert.Equal("""{"temperature":0.6,"top_k":20}""", command["abc_sampling"]!.ToJsonString());
        Assert.Equal("""{"top_p":0.9,"repetition_penalty":1.1,"penalty_window":40}""", command["semantic_sampling"]!.ToJsonString());
    }

    [Fact]
    public async Task Empty_sampling_overrides_are_left_out()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "x", semanticSampling = new { } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null((await _app.Worker.NextCommand())["semantic_sampling"]);
    }

    [Fact]
    public async Task Sampling_outside_the_models_ranges_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new
        {
            style = "Pop",
            lyrics = "x",
            fullSteps = 65,
            abcSampling = new { temperature = 6.0, topP = 0.0 },
            semanticSampling = new { topK = 0, repetitionPenalty = 0.0, penaltyWindow = 101 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(
            ["fullSteps", "abcSampling.temperature", "abcSampling.topP", "semanticSampling.topK", "semanticSampling.repetitionPenalty", "semanticSampling.penaltyWindow"],
            errors.Select(e => e.Key));
    }

    [Fact]
    public async Task The_status_tells_whether_the_worker_runs_with_the_extension()
    {
        Assert.Null((await _client.GetFromJsonAsync<StatusSnapshot>("/api/status", TestApp.Json))!.Worker.Extensions);

        await Generate();
        _app.Worker.Emit(new { @event = "ready", yueui_extensions = true });

        var status = await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Ready);
        Assert.True(status.Worker.Extensions);
    }

    [Fact]
    public async Task A_worker_without_the_extension_is_reported_as_such()
    {
        await Generate();
        _app.Worker.Emit(new { @event = "ready" });

        var status = await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Ready);
        Assert.False(status.Worker.Extensions);
    }

    [Fact]
    public void The_worker_extension_ships_with_the_app()
    {
        Assert.True(File.Exists(PythonWorkerLauncher.ExtensionScript), PythonWorkerLauncher.ExtensionScript);
    }

    [Fact]
    public async Task A_second_command_reuses_the_running_worker()
    {
        await Generate();
        await Generate();

        Assert.Equal(1, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Invalid_requests_are_refused_without_starting_a_worker()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "", lyrics = "x", batch = 0, quality = "best", cot = "maybe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(["style", "batch", "quality", "cot"], errors.Select(e => e.Key));
        Assert.Equal(0, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Worker_events_become_song_states()
    {
        await StartSong();
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "tokens", detail = "writing", engine = "gpu" });
        _app.Worker.Emit(new { @event = "progress", path = _app.AudioPath(Run, "song1"), fraction = 0.5, detail = "4000 tokens" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Any(song => song.Fraction == 0.5));

        Assert.Equal(WorkerStatus.Ready, status.Worker.Status);
        Assert.True(status.Worker.Busy);
        var song = Assert.Single(status.Songs);
        Assert.Equal($"{Run}/song1", song.Id);
        Assert.Equal("Neon Night", song.Title);
        Assert.Equal(831001, song.Seed);
        Assert.Equal("tokens", song.Stage);
        Assert.Equal("4000 tokens", song.Detail);
        Assert.Equal("gpu", song.Engine);
    }

    [Fact]
    public async Task A_finished_song_carries_its_length_and_quality()
    {
        await StartSong();
        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 187.5, quality = "draft", seed = 831001 });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Any(song => song.Finished));

        var song = Assert.Single(status.Songs);
        Assert.Equal(187.5, song.Seconds);
        Assert.Equal("draft", song.Quality);
        Assert.Equal(1, song.Fraction);
        Assert.False(status.Worker.Busy);
    }

    [Fact]
    public async Task A_song_keeps_its_stages_in_order_with_their_start()
    {
        await StartSong();
        var path = _app.AudioPath(Run, "song1");
        _app.Worker.Emit(new { @event = "stage", path, stage = "planning", detail = "" });
        _app.Worker.Emit(new { @event = "stage", path, stage = "tokens", detail = "writing" });
        // Same stage again with a new detail: no new step.
        _app.Worker.Emit(new { @event = "stage", path, stage = "tokens", detail = "writing more" });
        _app.Worker.Emit(new { @event = "failed", path, message = "out of memory" });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Any(song => song.Finished));

        var song = Assert.Single(status.Songs);
        Assert.Equal(["queued", "planning", "tokens", "failed"], song.Stages.Select(s => s.Stage));
        Assert.True(song.Stages.Zip(song.Stages.Skip(1)).All(pair => pair.First.StartedAt <= pair.Second.StartedAt));
        Assert.False(song.Render);
    }

    [Fact]
    public async Task A_rendered_song_is_marked_as_a_render()
    {
        _app.AddSong(Run, "song2", files: "semantic.npy");
        await _client.PostAsJsonAsync($"/api/songs/{Run}/song2/render", new { quality = "full" });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[] { new { index = 2, seed = 831002, path = _app.AudioPath(Run, "song2"), priority = 1 } },
        });

        var status = await _app.WaitForStatus(_client, s => s.Songs.Count == 1);

        Assert.True(Assert.Single(status.Songs).Render);
    }

    [Fact]
    public async Task Cancel_addresses_the_song_by_its_audio_path()
    {
        await StartSong();

        var response = await _client.PostAsync($"/api/songs/{Run}/song1/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("cancel", (string?)command["cmd"]);
        Assert.Equal(_app.AudioPath(Run, "song1"), (string?)command["path"]);
    }

    [Fact]
    public async Task Cancelling_a_song_that_is_not_queued_is_not_found()
    {
        var response = await _client.PostAsync($"/api/songs/{Run}/song1/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task When_the_worker_dies_its_songs_fail_and_the_next_command_starts_a_new_one()
    {
        await StartSong();

        _app.Worker.Exit();
        var status = await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Stopped);

        Assert.Equal("failed", Assert.Single(status.Songs).Stage);
        await Generate();
        Assert.Equal(2, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Render_needs_the_saved_tokens()
    {
        _app.AddSong(Run, "song1");

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song1/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Render_sends_the_song_folder()
    {
        var directory = _app.AddSong(Run, "song2", files: "semantic.npy");

        var response = await _client.PostAsJsonAsync($"/api/songs/{Run}/song2/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await _app.Worker.NextCommand();
        Assert.Equal("render", (string?)command["cmd"]);
        Assert.Equal(directory, (string?)command["path"]);
        Assert.Equal("full", (string?)command["quality"]);
    }

    [Theory]
    [InlineData("..", "song1")]
    [InlineData("transcriptions", "song1")]
    [InlineData(Run, "audio.flac")]
    public async Task Render_refuses_names_outside_the_library(string run, string song)
    {
        var response = await _client.PostAsJsonAsync($"/api/songs/{run}/{song}/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_song_the_worker_is_on_cannot_be_deleted_until_it_is_finished()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await StartSong();

        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/runs/{Run}")).StatusCode);
        // Another song of the run is not in the way.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Run}/song2")).StatusCode);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "cancelled", detail = "" });
        await _app.WaitForStatus(_client, s => s.Songs.All(song => song.Finished));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/runs/{Run}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.OutputDir, Run)));
    }

    [Fact]
    public async Task Deleting_tells_every_open_library_to_reload()
    {
        _app.AddSong(Run, "song1");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("snapshot", (await NextEvent(reader)).Type);

        await _client.DeleteAsync($"/api/runs/{Run}");

        Assert.Equal("library", (await NextEvent(reader)).Type);
    }

    [Fact]
    public async Task Status_reports_a_running_studio_app()
    {
        _app.Studio.Running = true;

        var status = await _app.WaitForStatus(_client, _ => true);

        Assert.True(status.Worker.StudioRunning);
        Assert.Equal(WorkerStatus.Stopped, status.Worker.Status);
    }

    [Fact]
    public async Task Shutdown_ends_the_worker()
    {
        await StartSong();

        var response = await _client.PostAsync("/api/worker/shutdown", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(_app.Worker.Disposed);
        await _app.WaitForStatus(_client, s => s.Worker.Status == WorkerStatus.Stopped);
    }

    [Fact]
    public async Task Event_stream_starts_with_a_snapshot_and_follows_the_worker()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal("snapshot", (await NextEvent(reader)).Type);

        await StartSong();
        (string Type, JsonObject Data) song;
        do
        {
            song = await NextEvent(reader);
        }
        while (song.Type != "song");
        Assert.Equal($"{Run}/song1", (string?)song.Data["id"]);
        Assert.Equal("queued", (string?)song.Data["stage"]);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private async Task<JsonObject> Generate()
    {
        var response = await _client.PostAsJsonAsync("/api/generate", new { style = "Dark synthwave", lyrics = "[verse]\nNeon", title = "Neon Night" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await _app.Worker.NextCommand();
    }

    /// <summary>A generate command and the worker's answer to it: ready, then one queued song.</summary>
    private async Task StartSong()
    {
        await Generate();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = new[] { new { index = 1, seed = 831001, path = _app.AudioPath(Run, "song1"), priority = 1 } },
        });
        await _app.WaitForStatus(_client, s => s.Songs.Count == 1);
    }

    private static async Task<(string Type, JsonObject Data)> NextEvent(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? type = null, data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
            else if (line.Length == 0 && type is not null)
            {
                return (type, JsonNode.Parse(data ?? "{}")!.AsObject());
            }
        }
        throw new EndOfStreamException();
    }
}
