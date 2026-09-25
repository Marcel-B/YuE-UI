using System.Net;
using System.Net.Http.Json;

namespace YueUI.Api.Tests;

public sealed class PlaylistEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public PlaylistEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task The_playlist_starts_empty()
    {
        var playlist = await _client.GetFromJsonAsync<PlaylistInfo>("/api/playlist");

        Assert.Empty(playlist!.SongIds);
    }

    [Fact]
    public async Task The_playlist_keeps_its_order_once_per_song_in_the_database()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");

        var response = await _client.PutAsJsonAsync("/api/playlist", new { songIds = new[] { $"{Run}/song2", $"{Run}/song1", $"{Run}/song2" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([$"{Run}/song2", $"{Run}/song1"], (await response.Content.ReadFromJsonAsync<PlaylistInfo>())!.SongIds);

        // A second server over the same database, as after a restart.
        using var restarted = new TestApp();
        restarted.AddSong(Run, "song1");
        restarted.AddSong(Run, "song2");
        File.Copy(Path.Combine(_app.Root, "yueui.db"), Path.Combine(restarted.Root, "yueui.db"));
        var playlist = await restarted.CreateClient().GetFromJsonAsync<PlaylistInfo>("/api/playlist");
        Assert.Equal([$"{Run}/song2", $"{Run}/song1"], playlist!.SongIds);
    }

    [Fact]
    public async Task A_deleted_song_drops_out_of_the_playlist()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        await _client.PutAsJsonAsync("/api/playlist", new { songIds = new[] { $"{Run}/song1", $"{Run}/song2" } });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/songs/{Run}/song1")).StatusCode);

        var playlist = await _client.GetFromJsonAsync<PlaylistInfo>("/api/playlist");
        Assert.Equal([$"{Run}/song2"], playlist!.SongIds);
    }

    [Theory]
    [InlineData("20260921-165850-Neon-Night/song9")]
    [InlineData("../../etc/passwd")]
    [InlineData("20260921-165850-Neon-Night")]
    public async Task Only_songs_of_the_library_are_accepted(string songId)
    {
        _app.AddSong(Run, "song1");

        var response = await _client.PutAsJsonAsync("/api/playlist", new { songIds = new[] { $"{Run}/song1", songId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<PlaylistInfo>("/api/playlist"))!.SongIds);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }
}
