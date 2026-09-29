using System.Net;
using System.Net.Http.Headers;

namespace YueUI.Api.Tests;

public sealed class StreamEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private string CopyPath(string song) => Path.Combine(_app.Root, "stream", Run, $"{song}.m4a");

    [Fact]
    public async Task A_song_streams_as_an_aac_copy_made_once()
    {
        _app.AddSong(Run, "song2");
        var client = _app.CreateClient();

        var first = await client.GetAsync($"/api/songs/{Run}/song2/stream");
        var second = await client.GetAsync($"/api/songs/{Run}/song2/stream");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("audio/mp4", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeEncoder.M4a, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(FakeEncoder.M4a, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal((1, 192_000), (_app.Encoder.Calls, _app.Encoder.BitRate));
        Assert.Equal(_app.AudioPath(Run, "song2"), _app.Encoder.Source);
        // Next to the database, never in YuE Studio's song folder.
        Assert.True(File.Exists(CopyPath("song2")));
        Assert.Null(first.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task The_copy_answers_ranges_and_is_checked_before_it_is_reused()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{Run}/song1/stream");
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var partial = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(FakeEncoder.M4a[..4], await partial.Content.ReadAsByteArrayAsync());
        Assert.True(partial.Headers.CacheControl!.NoCache);

        var again = new HttpRequestMessage(HttpMethod.Get, $"/api/songs/{Run}/song1/stream");
        again.Headers.IfNoneMatch.Add(partial.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task A_render_makes_the_copy_anew()
    {
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();
        await client.GetAsync($"/api/songs/{Run}/song1/stream");

        // A render writes the FLAC again in place.
        File.SetLastWriteTimeUtc(_app.AudioPath(Run, "song1"), File.GetLastWriteTimeUtc(CopyPath("song1")).AddMinutes(1));
        await client.GetAsync($"/api/songs/{Run}/song1/stream");

        Assert.Equal(2, _app.Encoder.Calls);
    }

    [Fact]
    public async Task Without_an_encoder_the_flac_plays()
    {
        _app.AddSong(Run, "song1");
        _app.Encoder.Available = false;

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(_app.AudioPath(Run, "song1")), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_failed_encoding_plays_the_flac_and_leaves_nothing_behind()
    {
        _app.AddSong(Run, "song1");
        _app.Encoder.Failure = "afconvert exited with 1: unsupported format";

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/stream");

        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.False(File.Exists(_app.Encoder.Target));
        Assert.False(File.Exists(CopyPath("song1")));
    }

    [Fact]
    public async Task Deleting_a_song_or_its_run_deletes_the_copies()
    {
        _app.AddSong(Run, "song1");
        _app.AddSong(Run, "song2");
        var client = _app.CreateClient();
        await client.GetAsync($"/api/songs/{Run}/song1/stream");
        await client.GetAsync($"/api/songs/{Run}/song2/stream");

        await client.DeleteAsync($"/api/songs/{Run}/song1");
        Assert.False(File.Exists(CopyPath("song1")));
        Assert.True(File.Exists(CopyPath("song2")));

        await client.DeleteAsync($"/api/runs/{Run}");
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "stream", Run)));
    }

    [Theory]
    [InlineData(Run, "song3")]
    [InlineData("..", "song1")]
    public async Task A_song_that_does_not_exist_is_not_found(string run, string song)
    {
        _app.AddSong(Run, "song1");

        var response = await _app.CreateClient().GetAsync($"/api/songs/{run}/{song}/stream");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _app.Encoder.Calls);
    }
}
