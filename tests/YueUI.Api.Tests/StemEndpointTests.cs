using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class StemEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_song_is_split_into_stems_that_can_be_heard_with_their_waveforms()
    {
        _app.AddSong(Run, "song2");
        _app.Stems.Files = ["vocals.wav", "drums.wav", "other.wav"];
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song2/stems", new { model = "htdemucs", dereverb = false });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<StemSetState>(TestApp.Json);
        Assert.Equal(("queued", "Neon Night", "htdemucs"), (queued!.Stage, queued.Title, queued.Model));

        var done = await WaitForStems(client, s => s.Finished);
        Assert.Equal("done", done.Stage);
        Assert.Equal("/api/separate", _app.Stems.RequestUri!.AbsolutePath);
        Assert.Equal("?model=htdemucs&dereverb=false", _app.Stems.RequestUri.Query);
        Assert.Equal(["vocals", "drums", "other"], done.Stems.Select(s => s.Name));
        Assert.Equal(["vocals.flac", "drums.flac", "other.flac"], done.Stems.Select(s => s.File));
        Assert.All(done.Stems, s => Assert.Equal(1, s.Seconds));
        // One scale for the set: the loudest stem reaches the top, the others stay below it.
        Assert.All(done.Stems[0].Peaks!, p => Assert.Equal(1, p));
        Assert.All(done.Stems[1].Peaks!, p => Assert.Equal(0.9, p, 2));
        Assert.Equal(400, done.Stems[0].Peaks!.Count);

        var audio = await client.GetAsync($"/api/songs/{Run}/song2/stems/{done.Id}/drums?download=true");
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal("audio/flac", audio.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeMixer.Flac, await audio.Content.ReadAsByteArrayAsync());
        Assert.Equal("Neon Night-song2-drums.flac", audio.Content.Headers.ContentDisposition!.FileNameStar);

        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{Run}/song2/stems/{done.Id}/vocals");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(range)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/songs/{Run}/song2/stems/{done.Id}/bass")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/songs/{Run}/song1/stems/{done.Id}/vocals")).StatusCode);
    }

    [Fact]
    public async Task Without_a_model_the_configured_one_separates()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        await client.PostAsync($"/api/songs/{Run}/song1/stems", JsonContent.Create(new { }));
        var done = await WaitForStems(client, s => s.Finished);

        Assert.Equal(("done", "mel-roformer-kim-vocals"), (done.Stage, done.Model));
        Assert.Equal("?model=mel-roformer-kim-vocals&dereverb=false", _app.Stems.RequestUri!.Query);
    }

    [Fact]
    public async Task A_stem_ffmpeg_cannot_encode_is_kept_as_the_WAV()
    {
        _app.AddSong(Run, "song1");
        _app.Mixer.Unencodable.Add("instrumental.wav");
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { dereverb = true });
        var done = await WaitForStems(client, s => s.Finished);

        Assert.Equal("instrumental.wav", done.Stems.Single(s => s.Name == "instrumental").File);
        var audio = await client.GetAsync($"/api/songs/{Run}/song1/stems/{done.Id}/instrumental");
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeStems.Wav(0.8), await audio.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_models_come_from_the_stem_service_with_this_servers_default()
    {
        _app.Stems.Models =
        [
            new JsonObject { ["id"] = "htdemucs", ["name"] = "HT Demucs", ["task"] = "4stem", ["stems"] = new JsonArray("vocals", "drums", "bass", "other"), ["isDefault"] = true },
            new JsonObject { ["id"] = "mel-roformer-kim-vocals", ["name"] = "Mel-RoFormer Kim", ["realtimeFactor"] = 1.5 },
        ];

        var models = await _app.CreateClient().GetFromJsonAsync<StemModel[]>("/api/stems/models", TestApp.Json);

        Assert.Equal(
            [("htdemucs", false), ("mel-roformer-kim-vocals", true)],
            models!.Select(m => (m.Id, m.IsDefault)));
        Assert.Equal(["vocals", "drums", "bass", "other"], models![0].Stems!);
    }

    [Fact]
    public async Task A_stem_service_without_a_model_list_offers_the_configured_model()
    {
        var models = await _app.CreateClient().GetFromJsonAsync<StemModel[]>("/api/stems/models", TestApp.Json);

        var model = Assert.Single(models!);
        Assert.Equal(("mel-roformer-kim-vocals", true), (model.Id, model.IsDefault));
    }

    [Fact]
    public async Task Without_a_stem_service_stems_are_off_even_with_voices()
    {
        _app.StemsBaseUrl = null;
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        var info = await client.GetFromJsonAsync<VoiceInfo>("/api/voice", TestApp.Json);
        var separate = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { });

        Assert.Equal((true, false, false), (info!.VoicesConfigured, info.ConversionConfigured, info.StemsConfigured));
        Assert.Equal(HttpStatusCode.NotImplemented, separate.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.GetAsync("/api/stems/models")).StatusCode);
    }

    [Fact]
    public async Task Stems_without_voices_are_still_on()
    {
        _app.VoiceBaseUrl = null;

        var info = await _app.CreateClient().GetFromJsonAsync<VoiceInfo>("/api/voice", TestApp.Json);

        Assert.Equal((false, true), (info!.VoicesConfigured, info.StemsConfigured));
    }

    [Fact]
    public async Task A_missing_song_or_odd_model_is_refused()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        var missing = await client.PostAsJsonAsync($"/api/songs/{Run}/song9/stems", new { });
        var odd = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { model = "a b" });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
        Assert.Equal(0, _app.Stems.Calls);
    }

    [Fact]
    public async Task While_stems_are_separated_songs_wait_and_the_song_stays()
    {
        _app.AddSong(Run, "song1");
        _app.Stems.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { });
        await _app.WaitForStatus(client, s => s.Stems!.Any(x => x.Stage == "separating"));

        var generate = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        var delete = await client.DeleteAsync($"/api/songs/{Run}/song1");

        Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(0, _app.Launcher.Launches);

        _app.Stems.Gate.SetResult();
        Assert.Equal("done", (await WaitForStems(client, s => s.Finished)).Stage);
        Assert.Equal("generate", (string?)(await (await _app.StartedWorker()).NextCommand())["cmd"]);
    }

    [Fact]
    public async Task Deleting_a_running_separation_stops_it()
    {
        _app.AddSong(Run, "song1");
        _app.Stems.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { });
        var set = await response.Content.ReadFromJsonAsync<StemSetState>(TestApp.Json);
        await _app.WaitForStatus(client, s => s.Stems!.Any(x => x.Stage == "separating"));

        var deleted = await client.DeleteAsync($"/api/songs/{Run}/song1/stems/{set!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await _app.WaitForStatus(client, s => s.Stems!.Single().Stage == "cancelled");
        Assert.Empty((await client.GetFromJsonAsync<StemSetState[]>("/api/stems", TestApp.Json))!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_song_takes_its_stems_along()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        var client = _app.CreateClient();
        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { });
        var done = await WaitForStems(client, s => s.Finished);
        var folder = Path.Combine(_app.Root, "stems", done.Id);
        Assert.True(File.Exists(Path.Combine(folder, "vocals_dry.flac")));

        await client.DeleteAsync($"/api/songs/{Run}/song1");

        Assert.False(Directory.Exists(folder));
        Assert.Empty((await client.GetFromJsonAsync<StemSetState[]>("/api/stems", TestApp.Json))!);
    }

    [Fact]
    public async Task A_failed_separation_says_why()
    {
        _app.AddSong(Run, "song1");
        _app.Stems.Status = HttpStatusCode.BadRequest;
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/stems", new { model = "nope" });
        var failed = await WaitForStems(client, s => s.Finished);

        Assert.Equal("failed", failed.Stage);
        Assert.Contains("Unbekanntes Modell", failed.Message);
    }

    /// <summary>The newest set, as the page lists it.</summary>
    private static async Task<StemSetState> WaitForStems(HttpClient client, Func<StemSetState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var sets = await client.GetFromJsonAsync<StemSetState[]>("/api/stems", TestApp.Json);
            if (sets?.FirstOrDefault() is { } set && condition(set))
            {
                return set;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The stems never got there: {sets?.FirstOrDefault()?.Stage}");
            }
            await Task.Delay(20);
        }
    }
}
