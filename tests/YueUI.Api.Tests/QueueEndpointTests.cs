using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using YueUI.Api.Data;
using YueUI.Api.Queue;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class QueueEndpointTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";

    private readonly TestApp _app = new();

    [Fact]
    public async Task Waiting_jobs_can_be_moved_and_taken_out()
    {
        var client = _app.CreateClient();
        var lyrics = await HoldLyricsAsync(client);
        var first = await Generate(client, "One");
        var second = await Generate(client, "Two");
        var third = await Generate(client, "Three");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/queue/{third.Id}/move", new { offset = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/queue/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/queue/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/queue/nope/move", new { offset = 1 })).StatusCode);

        Assert.Equal(["Three", "Two"], (await client.GetFromJsonAsync<List<QueuedJob>>("/api/queue", TestApp.Json))!.Select(j => j.Title));
        Assert.Equal(["Three", "Two"], _app.Snapshot().Queue!.Select(j => j.Title));

        lyrics.SetResult();
        Assert.Equal("Three", (string?)(await (await _app.StartedWorker()).NextCommand())["title"]);
        Assert.Equal("Two", (string?)(await (await _app.StartedWorker()).NextCommand())["title"]);
        await _app.WaitForStatus(client, s => s.Queue is { Count: 0 });
    }

    [Fact]
    public async Task A_render_waits_with_the_songs_title()
    {
        _app.AddSong(Run, "song2", title: "Neon Night", files: "semantic.npy");
        var client = _app.CreateClient();
        var lyrics = await HoldLyricsAsync(client);

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song2/render", new { quality = "full" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<QueuedJob>(TestApp.Json))!;
        Assert.Equal((JobKind.Render, "Neon Night", $"{Run}/song2", "full"), (job.Kind, job.Title, job.SongId, job.Quality));

        lyrics.SetResult();
        var command = await (await _app.StartedWorker()).NextCommand();
        Assert.Equal("render", (string?)command["cmd"]);
        Assert.Equal("full", (string?)command["quality"]);
        _app.Worker.Emit(new { @event = "started", job = Run, songs = new[] { new { index = 2, path = _app.AudioPath(Run, "song2") } } });
        Assert.True((await _app.WaitForStatus(client, s => s.Songs.Count == 1)).Songs[0].Render);
    }

    [Fact]
    public async Task Jobs_left_waiting_start_after_a_restart()
    {
        var store = new SqliteJobStore(new SqliteDatabase(Options.Create(new DataOptions { Path = Path.Combine(_app.Root, "yueui.db") })));
        var job = new QueuedJob("before", JobKind.Song, "Restarted", DateTimeOffset.UtcNow, Batch: 1, Quality: "draft");
        store.Add(job, new GenerateRequest("Pop", "[verse]\nLa", "Restarted").ToWorkerCommand());

        var client = _app.CreateClient();

        var command = await (await _app.StartedWorker()).NextCommand();
        Assert.Equal(("generate", "Restarted"), ((string?)command["cmd"], (string?)command["title"]));
        await _app.WaitForStatus(client, s => s.Queue is { Count: 0 });
        Assert.Empty(store.All());
    }

    [Fact]
    public async Task A_waiting_job_is_stored_until_it_starts()
    {
        var client = _app.CreateClient();
        var lyrics = await HoldLyricsAsync(client);
        var job = await Generate(client, "Kept");
        var store = new SqliteJobStore(new SqliteDatabase(Options.Create(new DataOptions { Path = Path.Combine(_app.Root, "yueui.db") })));

        var (stored, payload) = Assert.Single(store.All());
        Assert.Equal(job, stored);
        Assert.Equal("Kept", (string?)payload["title"]);

        lyrics.SetResult();
        await (await _app.StartedWorker()).NextCommand();
        await TestApp.WaitUntil(() => store.All().Count == 0);
    }

    [Fact]
    public async Task A_queued_song_that_cannot_start_leaves_the_queue_with_a_log_line()
    {
        var client = _app.CreateClient();
        var lyrics = await HoldLyricsAsync(client);
        await Generate(client, "Doomed");
        _app.Launcher.Fail = true;
        lyrics.SetResult();

        var status = await _app.WaitForStatus(client, s => s.Queue is { Count: 0 } && s.Log.Any(l => l.Level == "error"));
        Assert.Contains("Doomed", status.Log.Last(l => l.Level == "error").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_waiting_song_keeps_its_voice()
    {
        var client = _app.CreateClient();
        var lyrics = await HoldLyricsAsync(client);

        var response = await client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa", title = "Sung", voice = new { voiceId = "v1" } });

        var job = (await response.Content.ReadFromJsonAsync<QueuedJob>(TestApp.Json))!;
        Assert.Equal("Eurobecca", job.VoiceLabel);
        lyrics.SetResult();
        var command = await (await _app.StartedWorker()).NextCommand();
        Assert.Equal("Sung", (string?)command["title"]);
        Assert.False(command.ContainsKey("yueui_voice"));
        _app.Worker.Emit(new { @event = "started", job = Run, songs = new[] { new { index = 1, path = _app.AudioPath(Run, "song1") } } });
        var song = Assert.Single((await _app.WaitForStatus(client, s => s.Songs.Count == 1)).Songs);
        Assert.Equal(("v1", 0, 0.7), (song.Voice?.VoiceId, song.Voice?.SemiToneShift, song.Voice?.Strength));
    }

    public void Dispose() => _app.Dispose();

    /// <summary>A draft that holds the memory until the returned gate opens, so that what follows has to wait.</summary>
    private async Task<TaskCompletionSource> HoldLyricsAsync(HttpClient client)
    {
        var gate = _app.LmStudio.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = await client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer" });
        var id = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        await _app.WaitForStatus(client, s => s.Lyrics?.Id == id);
        return gate;
    }

    private static async Task<QueuedJob> Generate(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa", title });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QueuedJob>(TestApp.Json))!;
    }
}
