using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
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
        // Untrimmed: the fields stay out, so an older ChangeMyVoice sees the request it knows.
        Assert.False(_app.Voice.Form.ContainsKey("startSeconds"));
        Assert.False(_app.Voice.Form.ContainsKey("endSeconds"));
    }

    [Fact]
    public async Task A_trimmed_recording_passes_start_and_end_on()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Dark Female"), "label" },
            { new ByteArrayContent([.. "RIFF"u8, 1, 2]), "file", "dark.wav" },
            { new StringContent("4.5"), "startSeconds" },
            { new StringContent("21.25"), "endSeconds" },
        };

        var response = await _app.CreateClient().PostAsync("/api/voices", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("4.5", _app.Voice.Form["startSeconds"]);
        Assert.Equal("21.25", _app.Voice.Form["endSeconds"]);
    }

    [Theory]
    [InlineData("-1", "10")]
    [InlineData("10", "10")]
    [InlineData("12", "3")]
    public async Task A_trim_that_ends_before_it_starts_is_refused_here(string start, string end)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Dark Female"), "label" },
            { new ByteArrayContent([1]), "file", "a.wav" },
            { new StringContent(start), "startSeconds" },
            { new StringContent(end), "endSeconds" },
        };

        var response = await _app.CreateClient().PostAsync("/api/voices", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Voice.Requests);
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
    public async Task A_reference_voice_answers_range_requests_as_Safari_needs_them()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/voices/v1/audio");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);

        var response = await _app.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("RI"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
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
        // What it was made with stays with it, to tell versions apart later.
        Assert.Equal(
            ("v1", "Eurobecca", -12, 0.8, 30, true, "mel-roformer-kim-vocals"),
            (done.VoiceId, done.VoiceLabel, done.SemiToneShift, done.Strength, done.DiffusionSteps, done.KeepReverb, done.StemModel));

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

        // The player's AAC copy is made as soon as the version is done, and goes with it.
        var copy = Path.Combine(_app.Root, "versions", $"{done.Id}.m4a");
        await TestApp.WaitUntil(() => File.Exists(copy));
        Assert.Equal("Neon Night (Eurobecca)", _app.Tagger.Tags!.Title);
        var stream = await client.GetAsync($"/api/songs/{Run}/song2/versions/{done.Id}/stream");
        Assert.Equal("audio/mp4", stream.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeEncoder.M4a, await stream.Content.ReadAsByteArrayAsync());
        await client.DeleteAsync($"/api/songs/{Run}/song2/versions/{done.Id}");
        Assert.False(File.Exists(copy));
        Assert.False(File.Exists($"{copy}.tags"));
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
    public async Task While_a_voice_is_made_songs_and_lyrics_wait()
    {
        _app.AddSong(Run, "song1");
        _app.Stems.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        await WaitForVersion(client, $"{Run}/song1", v => v.Stage == "separating");

        var generate = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", title = "X" });
        var lyrics = await client.PostAsJsonAsync("/api/lyrics", new { keywords = "sea" });
        var delete = await client.DeleteAsync($"/api/songs/{Run}/song1");

        Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, lyrics.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(2, _app.Snapshot().Queue!.Count);
        Assert.Equal(0, _app.Launcher.Launches);
        Assert.Empty(_app.LmStudio.Requests);

        _app.Stems.Gate.SetResult();
        Assert.Equal("done", (await WaitForVersion(client, $"{Run}/song1", v => v.Finished)).Stage);
        Assert.Equal("generate", (string?)(await (await _app.StartedWorker()).NextCommand())["cmd"]);
        Assert.Single((await _app.WaitForStatus(client, s => s.Queue is { Count: 1 })).Queue!, j => j.Kind == Queue.JobKind.Lyrics);
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
    public async Task A_conversion_past_its_estimate_says_so_instead_of_standing_at_99_percent()
    {
        _app.AddSong(Run, "song1");
        // The fake job started a day ago and was expected to take ten minutes.
        _app.Voice.JobStatuses.Clear();
        _app.Voice.JobStatuses.Enqueue("RUNNING");
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/songs/{Run}/song1/versions", new { voiceId = "v1" });
        var version = await response.Content.ReadFromJsonAsync<VersionState>(TestApp.Json);
        // Progress is not stored, so it only travels with the status.
        var status = await _app.WaitForStatus(client, s => s.Versions!.Any(v => v.Stage == "converting" && v.Fraction > 0));

        var running = status.Versions!.Single();
        Assert.Equal((1, 600), (running.Fraction, running.EstimatedSeconds));
        await client.DeleteAsync($"/api/songs/{Run}/song1/versions/{version!.Id}");
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

    [Fact]
    public async Task Versions_from_before_the_stem_model_was_kept_are_still_listed()
    {
        _app.AddSong(Run, "song1");
        // The schema as it was before the stem model was kept, with one version made then.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_app.Root, "yueui.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $$"""
                CREATE TABLE playlists (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT '');
                CREATE TABLE playlist_songs (playlist_id INTEGER NOT NULL, position INTEGER NOT NULL, song_id TEXT NOT NULL,
                    PRIMARY KEY (playlist_id, position), UNIQUE (playlist_id, song_id));
                INSERT INTO playlists (id, name) VALUES (1, 'Playlist');
                CREATE TABLE run_titles (run_id TEXT PRIMARY KEY, title TEXT NOT NULL);
                CREATE TABLE song_ratings (song_id TEXT PRIMARY KEY, rating INTEGER NOT NULL);
                CREATE TABLE song_versions (id TEXT PRIMARY KEY, song_id TEXT NOT NULL, title TEXT NOT NULL, voice_id TEXT NOT NULL,
                    voice_label TEXT NOT NULL, semi_tone_shift INTEGER NOT NULL, strength REAL NOT NULL, diffusion_steps INTEGER NOT NULL,
                    keep_reverb INTEGER NOT NULL, stage TEXT NOT NULL, message TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO song_versions VALUES ('old', '{{Run}}/song1', 'Neon Night', 'v1', 'Eurobecca', 12, 0.5, 100, 0, 'done', NULL,
                    '2026-09-26T10:00:00.0000000+00:00', '2026-09-26T10:20:00.0000000+00:00');
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }

        var version = (await Library(_app.CreateClient())).SelectMany(r => r.Songs).Single(s => s.Id == $"{Run}/song1").Versions.Single();

        Assert.Equal(("old", 12, 0.5, 100, false), (version.Id, version.SemiToneShift, version.Strength, version.DiffusionSteps, version.KeepReverb));
        Assert.Null(version.StemModel);
    }

    [Fact]
    public async Task A_voice_chosen_in_the_form_sings_each_song_once_it_is_ready()
    {
        const string run = "20260927-180000-Neon-Night";
        _app.AddSong(run, "song1");
        _app.AddSong(run, "song2");
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/generate", new
        {
            style = "pop",
            lyrics = "[verse]\nLa",
            title = "Neon Night",
            batch = 2,
            voice = new { voiceId = "v1", semiToneShift = -12, strength = 0.9 },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await (await _app.StartedWorker()).NextCommand();
        // The worker does not know about voices.
        Assert.False(command.ContainsKey("yueui_voice"));
        Assert.False(command.ContainsKey("voice"));
        _app.Worker.Emit(new
        {
            @event = "started",
            job = run,
            title = "Neon Night",
            songs = new[] { new { path = _app.AudioPath(run, "song1"), index = 1 }, new { path = _app.AudioPath(run, "song2"), index = 2 } },
        });
        var started = await _app.WaitForStatus(client, s => s.Songs.Count == 2);
        Assert.All(started.Songs, s => Assert.Equal("Eurobecca", s.Voice?.VoiceLabel));

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(run, "song1"), stage = "ready" });
        await WaitForVersion(client, $"{run}/song1", v => v.Stage == "queued");
        // The second song still holds YuE2; the version waits for it.
        await Task.Delay(100);
        Assert.Equal(0, _app.Stems.Calls);

        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(run, "song2"), stage = "ready" });
        var first = await WaitForVersion(client, $"{run}/song1", v => v.Finished);
        var second = await WaitForVersion(client, $"{run}/song2", v => v.Finished);
        Assert.Equal(("done", "done"), (first.Stage, second.Stage));
        Assert.Equal(("v1", -12, 0.9, 50, true), (second.VoiceId, second.SemiToneShift, second.Strength, second.DiffusionSteps, second.KeepReverb));
        Assert.Equal("Neon Night", second.Title);

        // Rendering a song again does not make another version.
        _app.AddSong(run, "song1", files: "semantic.npy");
        await client.PostAsJsonAsync($"/api/songs/{run}/song1/render", new { quality = "full" });
        Assert.Equal("render", (string?)(await (await _app.StartedWorker()).NextCommand())["cmd"]);
        _app.Worker.Emit(new { @event = "started", job = run, songs = new[] { new { path = _app.AudioPath(run, "song1"), index = 1 } } });
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath(run, "song1"), stage = "ready" });
        await _app.WaitForStatus(client, s => s.Songs.Single(x => x.Id == $"{run}/song1") is { Render: true, Stage: "ready" });
        await Task.Delay(100);
        Assert.Single((await Library(client)).Single(r => r.Id == run).Songs.Single(s => s.Index == 1).Versions);
    }

    [Fact]
    public async Task A_song_with_an_unknown_voice_or_without_voices_is_refused()
    {
        var client = _app.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", voice = new { voiceId = "nobody" } });
        var steps = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", voice = new { voiceId = "v1", diffusionSteps = 1 } });

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("voice.voiceId", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, steps.StatusCode);
        Assert.Contains("voice.diffusionSteps", await steps.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, _app.Launcher.Launches);
    }

    [Fact]
    public async Task Without_voices_a_song_with_a_voice_is_refused()
    {
        _app.VoiceBaseUrl = null;
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/generate", new { style = "pop", lyrics = "[verse]\nLa", voice = new { voiceId = "v1" } });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal(0, _app.Launcher.Launches);
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
