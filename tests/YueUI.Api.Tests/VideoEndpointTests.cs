using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Video;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class VideoEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_video_is_rendered_from_the_layers_and_kept_to_download()
    {
        _app.AddSong("20260101-120000-neon", "song2");
        var client = _app.CreateClient();

        var response = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song2/videos",
            Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.Particles, showTitle: true, color: "#A78BFA"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<VideoState>(TestApp.Json);
        Assert.Equal(("20260101-120000-neon/song2", "Neon Night", "landscape", "bars", "#a78bfa", "particles", true, true),
            (queued!.SongId, queued.Title, queued.Format, queued.Effect, queued.Color, queued.Motion, queued.ShowCover, queued.ShowTitle));

        var done = await WaitForVideo(client, "20260101-120000-neon/song2", v => v.Finished);
        Assert.Equal(("done", (long?)FakeVideoRenderer.Mp4.Length), (done.Stage, done.Bytes));
        Assert.Equal(187.5, _app.Videos.Seconds);
        var layers = _app.Videos.Layers!;
        Assert.All(new[] { layers.Background, layers.Cover!, layers.Title!, Assert.Single(layers.Motion) }, path => Assert.True(File.Exists(path), path));

        var file = await client.GetAsync($"/api/videos/{done.Id}/file?download=true");
        Assert.Equal("video/mp4", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeVideoRenderer.Mp4, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("Neon Night-song2-youtube.mp4", file.Content.Headers.ContentDisposition!.FileNameStar);
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/videos/{done.Id}/file");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(range)).StatusCode);

        var status = (await client.GetFromJsonAsync<StatusSnapshot>("/api/status", TestApp.Json))!;
        Assert.Equal("done", Assert.Single(status.Videos!).Stage);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/videos/{done.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<VideoState[]>("/api/songs/20260101-120000-neon/song2/videos", TestApp.Json))!);
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "videos", done.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/videos/{done.Id}/file")).StatusCode);
    }

    [Fact]
    public async Task A_plasma_ball_without_cover_comes_as_frames_of_the_whole_picture()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();
        var layout = VideoLayout.For(VideoFormats.Portrait);

        var response = await client.PostAsync(
            "/api/songs/20260101-120000-neon/song1/videos",
            Form(VideoFormats.Portrait, VideoEffects.Wave, VideoMotions.Plasma, showTitle: false, showCover: false));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var done = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Finished);
        Assert.Equal(("done", "plasma", false), (done.Stage, done.Motion, done.ShowCover));
        var layers = _app.Videos.Layers!;
        Assert.Null(layers.Cover);
        Assert.Null(layers.Title);
        Assert.Equal(
            Enumerable.Range(0, VideoLayout.PlasmaFrames).Select(i => Path.Combine(_app.Root, "videos", done.Id, $"plasma{i}.png")),
            layers.Motion);
        foreach (var frame in layers.Motion)
        {
            Assert.Equal((layout.Width, layout.Height), VideoEndpoints.PngSize(await File.ReadAllBytesAsync(frame)));
        }
    }

    [Fact]
    public async Task Layers_must_be_PNGs_of_the_frame_size_and_the_settings_known_ones()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form("square", "lasers", "fireworks", false, color: "red;x"));
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(errors.ContainsKey("format"));
        Assert.True(errors.ContainsKey("effect"));
        Assert.True(errors.ContainsKey("motion"));
        Assert.True(errors.ContainsKey("color"));

        // A landscape background for a vertical video, a cover that is no PNG, no title although one was asked for and
        // a plasma ball of one frame, and that of the whole picture although it goes behind a cover.
        var wrong = new MultipartFormDataContent
        {
            { new StringContent("portrait"), "format" },
            { new StringContent("none"), "effect" },
            { new StringContent("plasma"), "motion" },
            { new StringContent("true"), "showTitle" },
            { new ByteArrayContent(Png(1920, 1080)), "background", "background.png" },
            { new ByteArrayContent("not a png"u8.ToArray()), "cover", "cover.png" },
            { new ByteArrayContent(Png(1080, 1920)), "plasma0", "plasma0.png" },
        };
        errors = (await (await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", wrong)).Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject();
        Assert.Equal(
            ["background", "cover", .. Enumerable.Range(0, VideoLayout.PlasmaFrames).Select(i => $"plasma{i}"), "title"],
            errors.Select(e => e.Key).Order());
        Assert.Empty((await client.GetFromJsonAsync<VideoState[]>("/api/songs/20260101-120000-neon/song1/videos", TestApp.Json))!);
    }

    [Fact]
    public async Task An_unknown_song_is_not_found_and_without_ffmpeg_nothing_is_queued()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        var client = _app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/songs/20260101-120000-neon/song9/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/songs/../song1/videos")).StatusCode);

        _app.Videos.Available = false;
        var response = await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false));
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_render_says_why_and_keeps_no_file()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        _app.Videos.Failure = "Unknown encoder 'libx264'";
        var client = _app.CreateClient();

        await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Portrait, VideoEffects.None, VideoMotions.None, false));
        var failed = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Finished);

        Assert.Equal(("failed", "Unknown encoder 'libx264'"), (failed.Stage, failed.Message));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/videos/{failed.Id}/file")).StatusCode);
    }

    [Fact]
    public async Task A_video_in_the_works_keeps_the_server_busy_until_it_is_deleted()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        _app.Videos.Gate = new TaskCompletionSource();
        var client = _app.CreateClient();

        await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Landscape, VideoEffects.Wave, VideoMotions.None, false));
        var rendering = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Stage == "rendering");
        var busy = (await client.GetFromJsonAsync<BusyInfo>("/api/busy", TestApp.Json))!;
        Assert.True(busy.Busy);
        Assert.Equal(["video"], busy.Reasons);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/videos/{rendering.Id}")).StatusCode);
        await _app.WaitForStatus(client, s => s.Videos is [{ Stage: "cancelled" }]);
        await TestApp.WaitUntil(() => !Directory.Exists(Path.Combine(_app.Root, "videos", rendering.Id)));
        Assert.False((await client.GetFromJsonAsync<BusyInfo>("/api/busy", TestApp.Json))!.Busy);
    }

    [Fact]
    public async Task Deleting_the_song_deletes_its_videos()
    {
        _app.AddSong("20260101-120000-neon", "song1");
        _app.AddSong("20260101-120000-neon", "song2");
        var client = _app.CreateClient();

        await client.PostAsync("/api/songs/20260101-120000-neon/song1/videos", Form(VideoFormats.Landscape, VideoEffects.Bars, VideoMotions.None, false));
        var done = await WaitForVideo(client, "20260101-120000-neon/song1", v => v.Finished);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/songs/20260101-120000-neon/song1")).StatusCode);

        Assert.False(Directory.Exists(Path.Combine(_app.Root, "videos", done.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/videos/{done.Id}/file")).StatusCode);
    }

    private static MultipartFormDataContent Form(string format, string effect, string motion, bool showTitle, bool showCover = true, string? color = null)
    {
        var layout = VideoLayout.For(format);
        var form = new MultipartFormDataContent
        {
            { new StringContent(format), "format" },
            { new StringContent(effect), "effect" },
            { new StringContent(color ?? ""), "color" },
            { new StringContent(motion), "motion" },
            { new StringContent(showCover ? "true" : "false"), "showCover" },
            { new StringContent(showTitle ? "true" : "false"), "showTitle" },
        };
        var names = new List<string> { "background" };
        if (showCover)
        {
            names.Add("cover");
        }
        if (showTitle)
        {
            names.Add("title");
        }
        if (motion == VideoMotions.Particles)
        {
            names.Add(motion);
        }
        foreach (var name in names)
        {
            form.Add(new ByteArrayContent(Png(layout.Width, layout.Height)), name, $"{name}.png");
        }
        if (motion == VideoMotions.Plasma)
        {
            var area = layout.PlasmaArea(showCover);
            for (var i = 0; i < VideoLayout.PlasmaFrames; i++)
            {
                form.Add(new ByteArrayContent(Png(area.Width, area.Height)), $"plasma{i}", $"plasma{i}.png");
            }
        }
        return form;
    }

    /// <summary>A PNG's signature and header chunk, which is all the server reads of it.</summary>
    private static byte[] Png(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        data[24] = 8;
        data[25] = 6;
        return data;
    }

    private static async Task<VideoState> WaitForVideo(HttpClient client, string songId, Func<VideoState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var videos = await client.GetFromJsonAsync<VideoState[]>($"/api/songs/{songId}/videos", TestApp.Json);
            if (videos?.FirstOrDefault() is { } video && condition(video))
            {
                return video;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The video never got there: {videos?.FirstOrDefault()?.Stage}");
            }
            await Task.Delay(20);
        }
    }
}
