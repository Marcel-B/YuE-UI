using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Data;
using YueUI.Api.Library;

namespace YueUI.Api.Tests;

public sealed class CoverEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 4, 5];

    private readonly TestApp _app = new();

    private readonly HttpClient _client;

    public CoverEndpointTests() => _client = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_song_keeps_its_cover_until_it_is_taken_away()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");

        var response = await _client.PutAsync($"/api/songs/{Run}/song2/cover", Form(Jpeg));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var listed = await Songs();
        Assert.Null(listed[0].CoverUpdatedAt);
        Assert.NotNull(listed[1].CoverUpdatedAt);
        var cover = await _client.GetAsync($"/api/songs/{Run}/song2/cover");
        Assert.Equal("image/jpeg", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Jpeg, await cover.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Run}/song2/cover")).StatusCode);
        Assert.Null((await Songs())[1].CoverUpdatedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/songs/{Run}/song2/cover")).StatusCode);
        Assert.Empty(Directory.GetFiles(Covers.Folder));
    }

    [Fact]
    public async Task A_new_cover_replaces_the_old_one_and_its_file()
    {
        _app.AddSong(Run, "song1");
        await _client.PutAsync($"/api/songs/{Run}/song1/cover", Form(Jpeg));
        var first = (await Songs())[0].CoverUpdatedAt;

        await _client.PutAsync($"/api/songs/{Run}/song1/cover", Form(Png));

        var cover = await _client.GetAsync($"/api/songs/{Run}/song1/cover");
        Assert.Equal("image/png", cover.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await cover.Content.ReadAsByteArrayAsync());
        Assert.True((await Songs())[0].CoverUpdatedAt > first);
        Assert.Single(Directory.GetFiles(Covers.Folder));
    }

    [Fact]
    public async Task Other_images_are_refused()
    {
        _app.AddSong(Run, "song1");

        var response = await _client.PutAsync($"/api/songs/{Run}/song1/cover", Form([(byte)'G', (byte)'I', (byte)'F', (byte)'8']));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await Songs())[0].CoverUpdatedAt);
    }

    [Theory]
    [InlineData(Run, "song3")]
    [InlineData("..", "song1")]
    public async Task A_song_that_does_not_exist_has_no_cover(string run, string song)
    {
        _app.AddSong(Run, "song1");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsync($"/api/songs/{run}/{song}/cover", Form(Jpeg))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/songs/{run}/{song}/cover")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/songs/{run}/{song}/cover")).StatusCode);
    }

    [Fact]
    public async Task An_export_without_a_chosen_cover_takes_the_songs_own()
    {
        _app.AddSong(Run, "song1");
        await _client.PutAsync($"/api/songs/{Run}/song1/cover", Form(Png));
        var form = new MultipartFormDataContent { { new StringContent("flac"), "format" } };

        var response = await _client.PostAsync($"/api/songs/{Run}/song1/export", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Png, _app.Tagger.Tags!.Cover!.Data);
        Assert.Equal("image/png", _app.Tagger.Tags.Cover.MimeType);
    }

    [Fact]
    public async Task Deleted_songs_and_runs_take_their_covers_along()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await _client.PutAsync($"/api/songs/{Run}/song1/cover", Form(Jpeg));
        await _client.PutAsync($"/api/songs/{Run}/song2/cover", Form(Jpeg));

        await _client.DeleteAsync($"/api/songs/{Run}/song1");
        Assert.Single(Covers.All());

        await _client.DeleteAsync($"/api/runs/{Run}");
        Assert.Empty(Covers.All());
        Assert.Empty(Directory.GetFiles(Covers.Folder));
    }

    private SqliteCoverStore Covers => _app.Services.GetRequiredService<SqliteCoverStore>();

    private async Task<IReadOnlyList<SongInfo>> Songs() =>
        Assert.Single((await _client.GetFromJsonAsync<List<RunInfo>>("/api/library", TestApp.Json))!).Songs;

    private static MultipartFormDataContent Form(byte[] cover)
    {
        var content = new ByteArrayContent(cover);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent { { content, "cover", "cover.jpg" } };
    }
}
