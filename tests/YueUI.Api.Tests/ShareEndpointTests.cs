using System.Net;

namespace YueUI.Api.Tests;

public sealed class ShareEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_song_is_shared_as_a_small_m4a_named_after_its_title()
    {
        _app.AddSong(Run, "song2", title: "Neon: Night?");

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song2/share");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Neon Night-song2.m4a", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(FakeEncoder.M4a, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(_app.AudioPath(Run, "song2"), _app.Encoder.Source);
        // The temporary file goes once it is sent.
        await TestApp.WaitUntil(() => !File.Exists(_app.Encoder.Target));
    }

    [Fact]
    public async Task Without_an_encoder_sharing_is_not_implemented()
    {
        _app.AddSong(Run, "song1");
        _app.Encoder.Available = false;

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/share");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_encoding_says_why_and_leaves_nothing_behind()
    {
        _app.AddSong(Run, "song1");
        _app.Encoder.Failure = "afconvert exited with 1: unsupported format";

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/share");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("unsupported format", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(_app.Encoder.Target));
    }

    [Theory]
    [InlineData(Run, "song3")]
    [InlineData("..", "song1")]
    public async Task A_song_that_does_not_exist_is_not_found(string run, string song)
    {
        _app.AddSong(Run, "song1");

        var response = await _app.CreateClient().GetAsync($"/api/songs/{run}/{song}/share");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_app.Encoder.Source);
    }
}
