using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using YueUI.Api.Library;

namespace YueUI.Api.Tests;

public sealed class LibraryEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public LibraryEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task Library_lists_runs_newest_first_with_their_songs()
    {
        _app.AddSong("20260920-080000-Old-Song", "song1", title: "Old Song");
        _app.AddSong("20260921-165850-Neon-Night", "song2", title: "Neon Night", quality: "full");
        _app.AddSong("20260921-165850-Neon-Night", "song10", files: "semantic.npy");
        _app.AddSong("20260921-165850-Neon-Night", "song1");
        // Not the worker's: transcriptions and anything else in the folder are left out.
        Directory.CreateDirectory(Path.Combine(_app.OutputDir, "transcriptions", "song1"));

        var runs = (await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!;

        Assert.Equal(["20260921-165850-Neon-Night", "20260920-080000-Old-Song"], runs.Select(r => r.Id));
        var run = runs[0];
        Assert.Equal("Neon Night", run.Title);
        Assert.Equal("Dark synthwave", run.Style);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 21, 16, 58, 50, DateTimeKind.Local)), run.CreatedAt);
        Assert.Equal([1, 2, 10], run.Songs.Select(s => s.Index));
        Assert.Equal("full", run.Songs[1].Quality);
        Assert.Equal(187.5, run.Songs[1].Seconds);
        Assert.Equal(42, run.Songs[1].Seed);
        Assert.True(run.Songs[2].CanRender);
        Assert.False(run.Songs[0].CanRender);
    }

    [Fact]
    public async Task A_renamed_run_keeps_its_folder_and_ids_and_shows_the_new_title_everywhere()
    {
        const string run = "20260921-165850-Neon-Night";
        _app.AddSong(run, "song1");
        await _client.PutAsJsonAsync("/api/playlist", new { songIds = new[] { $"{run}/song1" } });

        var response = await _client.PutAsJsonAsync($"/api/runs/{run}/title", new { title = "  Neonnächte  " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(_app.OutputDir, run, "song1")));
        var listed = Assert.Single((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
        Assert.Equal(run, listed.Id);
        Assert.Equal("Neonnächte", listed.Title);
        Assert.Equal("Neon Night", listed.OriginalTitle);
        Assert.Equal([$"{run}/song1"], (await _client.GetFromJsonAsync<PlaylistInfo>("/api/playlist"))!.SongIds);
        var audio = await _client.GetAsync($"/api/songs/{run}/song1/audio?download=true");
        Assert.Equal("Neonnächte-song1.flac", audio.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task An_empty_title_returns_the_run_to_the_workers()
    {
        const string run = "20260921-165850-Neon-Night";
        _app.AddSong(run, "song1");
        await _client.PutAsJsonAsync($"/api/runs/{run}/title", new { title = "Neonnächte" });

        var response = await _client.PutAsJsonAsync($"/api/runs/{run}/title", new { title = " " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var listed = Assert.Single((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
        Assert.Equal("Neon Night", listed.Title);
    }

    [Fact]
    public async Task A_deleted_run_forgets_its_title()
    {
        const string run = "20260921-165850-Neon-Night";
        _app.AddSong(run, "song1");
        await _client.PutAsJsonAsync($"/api/runs/{run}/title", new { title = "Neonnächte" });

        await _client.DeleteAsync($"/api/runs/{run}");
        _app.AddSong(run, "song1");

        var listed = Assert.Single((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
        Assert.Equal("Neon Night", listed.Title);
    }

    [Theory]
    [InlineData("20260921-165850-Other-Song")]
    [InlineData("..")]
    public async Task Renaming_a_run_that_does_not_exist_is_not_found(string run)
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _client.PutAsJsonAsync($"/api/runs/{run}/title", new { title = "Neonnächte" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_title_longer_than_a_line_is_refused()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _client.PutAsJsonAsync("/api/runs/20260921-165850-Neon-Night/title", new { title = new string('a', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_library_folder_is_an_empty_library()
    {
        Directory.Delete(_app.OutputDir, recursive: true);

        var runs = await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json);

        Assert.Empty(runs!);
    }

    [Fact]
    public async Task Audio_is_served_with_range_support()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/songs/20260921-165850-Neon-Night/song1/audio");
        request.Headers.Range = new RangeHeaderValue(0, 3);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("fLaC"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Downloads_are_named_after_the_title()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song2", title: "Neon: Night?");

        var response = await _client.GetAsync("/api/songs/20260921-165850-Neon-Night/song2/audio?download=true");

        Assert.Equal("Neon Night-song2.flac", response.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task Score_is_served_as_a_download()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _client.GetAsync("/api/songs/20260921-165850-Neon-Night/song1/score");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("X:1\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("Neon Night-song1.abc", response.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task A_song_zips_its_audio_and_score()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song2");

        var response = await _client.GetAsync("/api/songs/20260921-165850-Neon-Night/song2/zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Neon Night-song2.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(["Neon Night-song2.flac", "Neon Night-song2.abc"], zip.Entries.Select(e => e.FullName));
        using var score = new StreamReader(zip.GetEntry("Neon Night-song2.abc")!.Open());
        Assert.Equal("X:1\n", await score.ReadToEndAsync());
    }

    [Fact]
    public async Task A_run_zips_all_its_songs()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song2");
        _app.AddSong("20260921-165850-Neon-Night", "song1");
        // A song that failed before its audio: its score still goes in.
        File.Delete(Path.Combine(_app.AddSong("20260921-165850-Neon-Night", "song3"), "audio.flac"));

        var response = await _client.GetAsync("/api/runs/20260921-165850-Neon-Night/zip");

        Assert.Equal("Neon Night.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["Neon Night-song1.flac", "Neon Night-song1.abc", "Neon Night-song2.flac", "Neon Night-song2.abc", "Neon Night-song3.abc"],
            zip.Entries.Select(e => e.FullName));
    }

    [Theory]
    [InlineData("/api/runs/20260921-165850-Other/zip")]
    [InlineData("/api/runs/transcriptions/zip")]
    [InlineData("/api/songs/20260921-165850-Neon-Night/song9/zip")]
    public async Task Zips_of_unknown_songs_are_not_found(string path)
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/songs/20260921-165850-Neon-Night/song9/audio")]
    [InlineData("/api/songs/..%2F..%2Fetc/song1/audio")]
    [InlineData("/api/songs/20260921-165850-Neon-Night/..%2Fsong1/audio")]
    public async Task Unknown_or_foreign_paths_are_not_found(string path)
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");

        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_library_says_what_songs_and_runs_take_on_disk()
    {
        // 100 bytes of audio, 4 of score, and the two JSON files.
        var song1 = _app.AddSong("20260921-165850-Neon-Night", "song1");
        _app.AddSong("20260921-165850-Neon-Night", "song2");
        File.WriteAllBytes(Path.Combine(song1, "semantic.npy"), new byte[1000]);
        File.WriteAllText(Path.Combine(_app.OutputDir, "20260921-165850-Neon-Night", "job.json"), "{}");

        var run = Assert.Single((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);

        var files = Directory.EnumerateFiles(_app.OutputDir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f));
        Assert.Equal(files.Where(f => f.DirectoryName == song1).Sum(f => f.Length), run.Songs[0].Bytes);
        Assert.Equal(run.Songs[1].Bytes + 1000, run.Songs[0].Bytes);
        Assert.Equal(files.Sum(f => f.Length), run.Bytes);
    }

    [Fact]
    public async Task Deleting_a_song_removes_its_folder_and_the_last_one_takes_the_run_along()
    {
        const string run = "20260921-165850-Neon-Night";
        var song1 = _app.AddSong(run, "song1");
        var song2 = _app.AddSong(run, "song2");

        var first = await _client.DeleteAsync($"/api/songs/{run}/song1");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.False(Directory.Exists(song1));
        Assert.True(Directory.Exists(song2));

        var last = await _client.DeleteAsync($"/api/songs/{run}/song2");

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.OutputDir, run)));
        Assert.Empty((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!);
    }

    [Fact]
    public async Task Deleting_a_run_removes_it_with_all_its_songs()
    {
        _app.AddSong("20260921-165850-Neon-Night", "song1");
        _app.AddSong("20260921-165850-Neon-Night", "song2");
        var other = _app.AddSong("20260920-080000-Old-Song", "song1");

        var response = await _client.DeleteAsync("/api/runs/20260921-165850-Neon-Night");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.OutputDir, "20260921-165850-Neon-Night")));
        Assert.True(Directory.Exists(other));
    }

    [Theory]
    [InlineData("/api/runs/20260921-165850-Other")]
    [InlineData("/api/runs/transcriptions")]
    [InlineData("/api/runs/..")]
    [InlineData("/api/runs/..%2Fsongs")]
    [InlineData("/api/songs/20260921-165850-Neon-Night/song9")]
    [InlineData("/api/songs/20260921-165850-Neon-Night/..%2Fsong1")]
    [InlineData("/api/songs/..%2F..%2Fetc/song1")]
    public async Task Only_songs_and_runs_of_the_library_can_be_deleted(string path)
    {
        var song = _app.AddSong("20260921-165850-Neon-Night", "song1");
        Directory.CreateDirectory(Path.Combine(_app.OutputDir, "transcriptions"));

        var response = await _client.DeleteAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(Directory.Exists(song));
        Assert.True(Directory.Exists(Path.Combine(_app.OutputDir, "transcriptions")));
    }

    [Fact]
    public async Task Storage_reports_the_space_of_the_library_volume()
    {
        // Also before the worker has written the folder.
        Directory.Delete(_app.OutputDir, recursive: true);

        var storage = (await _client.GetFromJsonAsync<StorageInfo>("/api/storage", TestApp.Json))!;

        Assert.InRange(storage.FreeBytes, 1, storage.TotalBytes);
    }

    [Fact]
    public async Task Without_YuE_Studio_generating_explains_what_is_missing()
    {
        using var app = new TestApp(fakeWorker: false);
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Yue:InstallRoot", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }
}
