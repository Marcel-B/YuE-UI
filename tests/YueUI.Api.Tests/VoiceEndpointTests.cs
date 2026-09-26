using System.Net;
using System.Net.Http.Json;
using YueUI.Api.Library;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class VoiceEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Reference_voices_come_from_the_voice_service_with_its_key()
    {
        var client = _app.CreateClient();

        var info = await client.GetFromJsonAsync<VoiceInfo>("/api/voice", TestApp.Json);
        var voices = await client.GetFromJsonAsync<ReferenceVoice[]>("/api/voices", TestApp.Json);

        Assert.True(info!.VoicesConfigured);
        Assert.True(info.ConversionConfigured);
        var voice = Assert.Single(voices!);
        Assert.Equal(("v1", "Eurobecca", 24.5), (voice.Id, voice.Label, voice.Seconds));
        Assert.Equal((HttpMethod.Get, "/api/v1/voices", "voice-key"), Assert.Single(_app.Voice.Requests));
    }

    [Fact]
    public async Task A_recording_becomes_a_reference_voice()
    {
        var client = _app.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(" Dark Female "), "label" },
            { new ByteArrayContent([.. "RIFF"u8, 1, 2]), "file", "dark.wav" },
        };

        var response = await client.PostAsync("/api/voices", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var voice = await response.Content.ReadFromJsonAsync<ReferenceVoice>(TestApp.Json);
        Assert.Equal(("v2", "Dark Female"), (voice!.Id, voice.Label));
        Assert.Equal("Dark Female", _app.Voice.Form["label"]);
        Assert.Equal("6 bytes", _app.Voice.Form["file"]);
    }

    [Fact]
    public async Task A_voice_without_a_name_is_refused_here()
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "a.wav" } };

        var response = await _app.CreateClient().PostAsync("/api/voices", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Voice.Requests);
    }

    [Fact]
    public async Task The_services_refusal_reaches_the_browser_with_its_reason()
    {
        var response = await _app.CreateClient().DeleteAsync("/api/voices/v1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Referenzstimme in Verwendung", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_reference_voice_can_be_heard_again()
    {
        var response = await _app.CreateClient().GetAsync("/api/voices/v1/audio");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal([.. "RIFF"u8, 9], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Without_a_voice_service_voices_are_off()
    {
        _app.VoiceBaseUrl = null;
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        var info = await client.GetFromJsonAsync<VoiceInfo>("/api/voice", TestApp.Json);
        var list = await client.GetAsync("/api/voices");
        var version = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });

        Assert.False(info!.VoicesConfigured);
        Assert.False(info.ConversionConfigured);
        Assert.Equal(HttpStatusCode.NotImplemented, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, version.StatusCode);
    }

    [Fact]
    public async Task A_song_is_separated_converted_and_mixed_into_a_version()
    {
        _app.AddSong(Run, "song2");
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/songs/{Run}/song2/versions", new { voiceId = "v1", semiToneShift = -12, strength = 0.8, diffusionSteps = 30 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<VersionState>(TestApp.Json);
        Assert.Equal(("queued", "Eurobecca", "Neon Night"), (queued!.Stage, queued.VoiceLabel, queued.Title));

        var done = await WaitForVersion(client, $"{Run}/song2", v => v.Finished);
        Assert.Equal("done", done.Stage);

        // Dry vocals from the stem service, with its model; the reverb goes back into the mix untouched.
        Assert.Equal("/api/separate", _app.Stems.RequestUri!.AbsolutePath);
        Assert.Equal("?model=mel-roformer-kim-vocals&dereverb=true", _app.Stems.RequestUri.Query);
        Assert.Equal("stems-key", _app.Stems.Key);
        Assert.Equal("v1", _app.Voice.Form["voiceId"]);
        Assert.Equal("-12", _app.Voice.Form["semiToneShift"]);
        Assert.Equal("0.8", _app.Voice.Form["inferenceCfgRate"]);
        Assert.Equal("30", _app.Voice.Form["diffusionSteps"]);
        Assert.Equal("48000", _app.Voice.Form["outputSampleRate"]);
        Assert.Equal(new MixInput("instrumental.wav", "converted.wav", "vocals_dry.wav", "vocals_reverb.wav"), _app.Mixer.Input);
        // The job's files at the service go as soon as the result is here.
        Assert.Equal(1, _app.Voice.DeletedJobs);

        var audio = await client.GetAsync($"/api/songs/{Run}/song2/versions/{done.Id}/audio?download=true");
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal(FakeMixer.Flac, await audio.Content.ReadAsByteArrayAsync());
        Assert.Equal("Neon Night-song2-Eurobecca.flac", audio.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task Without_reverb_and_shift_neither_is_sent()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1", keepReverb = false });
        await WaitForVersion(client, $"{Run}/song1", v => v.Finished);

        // An older ChangeMyVoice does not know the field and would refuse every job.
        Assert.False(_app.Voice.Form.ContainsKey("semiToneShift"));
        Assert.Null(_app.Mixer.Input!.Reverb);
    }

    [Fact]
    public async Task A_failed_conversion_says_why()
    {
        _app.AddSong(Run, "song1");
        _app.Voice.JobStatuses.Clear();
        _app.Voice.JobStatuses.Enqueue("FAILED");
        _app.Voice.JobError = "Zu wenig Arbeitsspeicher.";
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        var failed = await WaitForVersion(client, $"{Run}/song1", v => v.Finished);

        Assert.Equal(("failed", "Zu wenig Arbeitsspeicher."), (failed.Stage, failed.Message));
        Assert.Null(_app.Mixer.Input);
    }

    [Fact]
    public async Task An_unknown_voice_or_bad_setting_is_refused()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        var unknown = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "nobody" });
        var shifted = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1", semiToneShift = 30 });
        var missing = await client.PostAsJsonAsync($"/api/songs/{Run}/song9/versions", new { voiceId = "v1" });

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, shifted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, _app.Stems.Calls);
    }

    [Fact]
    public async Task While_a_voice_is_made_no_song_or_lyrics_start()
    {
        _app.AddSong(Run, "song1");
        _app.Stems.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        await WaitForVersion(client, $"{Run}/song1", v => v.Stage == "separating");

        var generate = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        var lyrics = await client.PostAsJsonAsync("/api/lyrics", new { keywords = "sea" });
        var delete = await client.DeleteAsync($"/api/songs/{Run}/song1");

        Assert.Equal(HttpStatusCode.Conflict, generate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lyrics.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        _app.Stems.Gate.SetResult();
        Assert.Equal("done", (await WaitForVersion(client, $"{Run}/song1", v => v.Finished)).Stage);
    }

    [Fact]
    public async Task A_version_waits_while_songs_are_generated()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "started", job = "20260926-120000-X", title = "X", songs = new[] { new { path = _app.AudioPath("20260926-120000-X", "song1"), index = 1 } } });
        await _app.WaitForStatus(client, s => s.Worker.Busy);

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        await Task.Delay(100);
        Assert.Equal(0, _app.Stems.Calls);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath("20260926-120000-X", "song1"), stage = "ready" });
        Assert.Equal("done", (await WaitForVersion(client, $"{Run}/song1", v => v.Finished)).Stage);
        // YuE2 leaves the memory before the separation takes it.
        Assert.True(_app.Worker.Disposed);
    }

    [Fact]
    public async Task Deleting_a_running_version_stops_it()
    {
        _app.AddSong(Run, "song1");
        _app.Stems.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        var version = await response.Content.ReadFromJsonAsync<VersionState>(TestApp.Json);
        await WaitForVersion(client, $"{Run}/song1", v => v.Stage == "separating");

        var deleted = await client.DeleteAsync($"/api/songs/{Run}/song1/versions/{version!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await _app.WaitForStatus(client, s => s.Versions!.Single().Stage == "cancelled");
        var song = (await Library(client)).Single().Songs.Single();
        Assert.Empty(song.Versions);
        // A song without a version in the works can go again.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_song_takes_its_versions_along()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        var client = _app.CreateClient();
        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        var done = await WaitForVersion(client, $"{Run}/song1", v => v.Finished);
        var file = Path.Combine(_app.Root, "versions", $"{done.Id}.flac");
        Assert.True(File.Exists(file));

        await client.DeleteAsync($"/api/songs/{Run}/song1");

        Assert.False(File.Exists(file));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/songs/{Run}/song1/versions/{done.Id}/audio")).StatusCode);
    }

    private static async Task<RunInfo[]> Library(HttpClient client) =>
        (await client.GetFromJsonAsync<RunInfo[]>("/api/library", TestApp.Json))!;

    /// <summary>The library lists every version of the song with its stage.</summary>
    private static async Task<VersionState> WaitForVersion(HttpClient client, string songId, Func<VersionState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var song = (await Library(client)).SelectMany(r => r.Songs).Single(s => s.Id == songId);
            if (song.Versions.LastOrDefault() is { } version && condition(version))
            {
                return version;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The version never got there: {string.Join(", ", song.Versions.Select(v => $"{v.Stage} {v.Message}"))}");
            }
            await Task.Delay(20);
        }
    }
}
