using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Push;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class PushEndpointTests : IDisposable
{
    private const string Run = "20260922-101500-Neon-Night";
    private const string Endpoint = "https://web.push.apple.com/QGuQyavXutnMH";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public PushEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task The_key_is_a_P256_point_made_once_and_kept_in_a_file_only_its_owner_reads()
    {
        var key = (await _client.GetFromJsonAsync<PushKey>("/api/push"))!.PublicKey;

        var point = Base64Url.DecodeFromChars(key);
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
        Assert.Equal(key, (await _client.GetFromJsonAsync<PushKey>("/api/push"))!.PublicKey);
        var file = Path.Combine(_app.Root, "push.json");
        Assert.Contains(key, await File.ReadAllTextAsync(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public async Task A_subscription_is_stored_once_per_endpoint_and_removed_again()
    {
        await Subscribe("de");
        await Subscribe("en");

        var store = Store;
        var subscription = Assert.Single(store.Subscriptions);
        Assert.Equal("en", subscription.Language);
        Assert.Equal("browser-public-key", subscription.P256dh);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/push/subscriptions") { Content = JsonContent.Create(new { endpoint = Endpoint }) };
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(request)).StatusCode);
        Assert.Empty(store.Subscriptions);
    }

    [Theory]
    [InlineData("http://push.example.com/abc")]
    [InlineData("https://127.0.0.1/abc")]
    [InlineData("not a url")]
    public async Task Only_https_push_services_are_accepted(string endpoint)
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscriptions", new { endpoint, keys = new { p256dh = "a", auth = "b" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_subscription_without_keys_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscriptions", new { endpoint = Endpoint });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_finished_song_is_announced_once_in_the_subscriptions_language()
    {
        await Subscribe("en");
        await StartSong();

        _app.Worker.Emit(new { @event = "song", index = 1, path = _app.AudioPath(Run, "song1"), seconds = 187.5, quality = "draft" });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });

        var (subscription, payload) = await _app.Push.Next();
        Assert.Equal(Endpoint, subscription.Endpoint);
        Assert.Equal("Song finished", (string?)payload["title"]);
        Assert.Equal("Neon Night · Song 1 · 3:08", (string?)payload["body"]);
        Assert.Equal($"song:{Run}/song1", (string?)payload["tag"]);
        Assert.Equal("/ui/", (string?)payload["url"]);

        // A second event for the finished song is no news.
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });
        _app.Worker.Emit(new { @event = "log", message = "flushed" });
        await _app.WaitForStatus(_client, s => s.Log.Any(entry => entry.Message == "flushed"));
        Assert.False(_app.Push.TryNext(out _));
    }

    [Fact]
    public async Task A_failed_song_says_why_and_a_cancelled_one_stays_silent()
    {
        await Subscribe("de");
        await StartSong(songs: 2);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "cancelled", detail = "" });
        _app.Worker.Emit(new { @event = "failed", path = _app.AudioPath(Run, "song2"), message = "Out of memory" });

        var (_, payload) = await _app.Push.Next();
        Assert.Equal("Song fehlgeschlagen", (string?)payload["title"]);
        Assert.Equal("Neon Night · Song 2: Out of memory", (string?)payload["body"]);
        Assert.False(_app.Push.TryNext(out _));
    }

    [Fact]
    public async Task A_finished_transcription_is_announced()
    {
        await Subscribe("de");
        _app.InstallSheetSage();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("ID3 audio"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", "My Song.mp3");
        form.Add(new StringContent("melody-full"), "task");
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync("/api/transcriptions", form)).StatusCode);
        var id = (string)(await _app.Worker.NextCommand())["id"]!;

        _app.Worker.Emit(new { @event = "transcribe", id, stage = "done", abc = "X:1\n" });

        var (_, payload) = await _app.Push.Next();
        Assert.Equal("Transkription fertig", (string?)payload["title"]);
        Assert.Equal("My Song.mp3", (string?)payload["body"]);
    }

    [Fact]
    public async Task A_finished_lyrics_draft_is_announced()
    {
        await Subscribe("en");

        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "rain" })).StatusCode);

        var (_, payload) = await _app.Push.Next();
        Assert.Equal("Lyrics ready", (string?)payload["title"]);
        Assert.Equal("lyrics", (string?)payload["tag"]);
    }

    [Fact]
    public async Task A_subscription_its_service_no_longer_knows_is_dropped()
    {
        await Subscribe("de");
        _app.Push.Result = PushResult.Gone;
        await StartSong();

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(Run, "song1"), stage = "ready", detail = "" });

        await _app.Push.Next();
        await TestApp.WaitUntil(() => Store.Subscriptions.Count == 0);
    }

    [Fact]
    public async Task A_test_notification_goes_to_that_browser_only()
    {
        await Subscribe("de");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/api/push/test", new { endpoint = Endpoint })).StatusCode);
        var (_, payload) = await _app.Push.Next();
        Assert.Equal("Benachrichtigungen funktionieren.", (string?)payload["body"]);

        var unknown = await _client.PostAsJsonAsync("/api/push/test", new { endpoint = "https://fcm.googleapis.com/fcm/send/other" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private PushStore Store => (PushStore)_app.Services.GetService(typeof(PushStore))!;

    private async Task Subscribe(string language)
    {
        var response = await _client.PostAsJsonAsync("/api/push/subscriptions", new
        {
            endpoint = Endpoint,
            expirationTime = (object?)null,
            // Stored as given; only the real sender (WebPushSenderTests) needs valid keys.
            keys = new { p256dh = "browser-public-key", auth = "browser-auth-secret" },
            language,
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task StartSong(int songs = 1)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa", title = "Neon Night" })).StatusCode);
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = Run,
            title = "Neon Night",
            songs = Enumerable.Range(1, songs).Select(i => new { index = i, seed = i, path = _app.AudioPath(Run, $"song{i}"), priority = 1 }).ToArray(),
        });
        await _app.WaitForStatus(_client, s => s.Songs.Count == songs);
    }
}
