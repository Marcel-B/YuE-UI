using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace YueUI.Api.Tests;

/// <summary>The cover chosen once in the library, and the lyrics, go into every file that leaves the app.</summary>
public sealed class TaggedDownloadTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TestApp _app = new();

    private readonly HttpClient _client;

    public TaggedDownloadTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_downloaded_flac_is_a_tagged_copy_and_the_songs_own_file_stays_as_it_is()
    {
        _app.AddSong(Run, "song1");
        await SetCover("song1");
        var original = await File.ReadAllBytesAsync(_app.AudioPath(Run, "song1"));

        var response = await _client.GetAsync($"/api/songs/{Run}/song1/audio?download=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Neon Night-song1.flac", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(original, await response.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(_app.AudioPath(Run, "song1"), _app.Tagger.Path);
        Assert.Equal(Jpeg, _app.Tagger.Tags!.Cover!.Data);
        Assert.Equal("[verse]\nLa la", _app.Tagger.Tags.Lyrics);
        await TestApp.WaitUntil(() => !File.Exists(_app.Tagger.Path));
    }

    [Fact]
    public async Task The_player_and_the_logic_page_get_the_untagged_file()
    {
        _app.AddSong(Run, "song1");

        var response = await _client.GetAsync($"/api/songs/{Run}/song1/audio");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _app.Tagger.Calls);
    }

    [Fact]
    public async Task A_download_that_cannot_be_tagged_still_arrives()
    {
        _app.AddSong(Run, "song1");
        _app.Tagger.Failure = "not a FLAC";

        var response = await _client.GetAsync($"/api/songs/{Run}/song1/audio?download=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(await File.ReadAllBytesAsync(_app.AudioPath(Run, "song1")), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_run_zip_tags_every_flac_and_leaves_no_copies_behind()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await SetCover("song2");

        var response = await _client.GetAsync($"/api/runs/{Run}/zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _app.Tagger.Calls);
        // The last one tagged is song2, with its cover.
        Assert.Equal(Jpeg, _app.Tagger.Tags!.Cover!.Data);
        Assert.False(File.Exists(_app.Tagger.Path));
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["Neon Night-song1.flac", "Neon Night-song1.abc", "Neon Night-song2.flac", "Neon Night-song2.abc"],
            zip.Entries.Select(e => e.FullName));
    }

    private async Task SetCover(string song)
    {
        var content = new ByteArrayContent(Jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var form = new MultipartFormDataContent { { content, "cover", "cover.jpg" } };
        var response = await _client.PutAsync($"/api/songs/{Run}/{song}/cover", form);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
