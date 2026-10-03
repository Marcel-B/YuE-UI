using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Voices;

namespace YueUI.Api.Tests;

public sealed class SwapEndpointTests : IDisposable
{
    private static readonly byte[] Upload = FakeStems.Wav(0.5);

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_vocal_track_is_sung_with_the_voice_without_a_separation()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsync("/api/swaps", Form(("voiceId", "v1"), ("semiToneShift", "12"), ("strength", "0.9"), ("diffusionSteps", "25")));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<SwapState>(TestApp.Json);
        Assert.Equal(("queued", "Eurobecca", "Mein Gesang.wav", false), (queued!.Stage, queued.VoiceLabel, queued.FileName, queued.Separate));

        var done = await WaitForSwap(client, s => s.Finished);
        Assert.Equal("done", done.Stage);
        Assert.Equal((12, 0.9, 25, false, (string?)null), (done.SemiToneShift, done.Strength, done.DiffusionSteps, done.KeepReverb, done.StemModel));
        Assert.Equal(0, _app.Stems.Calls);
        Assert.Null(_app.Mixer.Input);
        Assert.Equal(["vocals.wav"], _app.Mixer.Decoded);
        Assert.Equal(["converted.wav"], _app.Mixer.Encoded);
        Assert.Equal($"{Upload.Length} bytes", _app.Voice.Form["source"]);
        Assert.Equal(("v1", "12", "0.9", "25"), (_app.Voice.Form["voiceId"], _app.Voice.Form["semiToneShift"], _app.Voice.Form["inferenceCfgRate"], _app.Voice.Form["diffusionSteps"]));

        var audio = await client.GetAsync($"/api/swaps/{done.Id}/audio?download=true");
        Assert.Equal("audio/flac", audio.Content.Headers.ContentType!.MediaType);
        Assert.Equal(FakeMixer.Flac, await audio.Content.ReadAsByteArrayAsync());
        Assert.Equal("Mein Gesang-Eurobecca.flac", audio.Content.Headers.ContentDisposition!.FileNameStar);

        // The upload stays beside the result, to compare.
        var source = await client.GetAsync($"/api/swaps/{done.Id}/source");
        Assert.Equal("audio/wav", source.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Upload, await source.Content.ReadAsByteArrayAsync());
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/swaps/{done.Id}/audio");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        Assert.Equal(HttpStatusCode.PartialContent, (await client.SendAsync(range)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/swaps/{done.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<SwapState[]>("/api/swaps", TestApp.Json))!);
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "swaps", done.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/swaps/{done.Id}/source")).StatusCode);
    }

    [Fact]
    public async Task A_whole_song_is_separated_and_the_new_vocals_mixed_back()
    {
        var client = _app.CreateClient();

        await client.PostAsync("/api/swaps", Form(("voiceId", "v1"), ("separate", "true"), ("keepReverb", "false")));
        var done = await WaitForSwap(client, s => s.Finished);

        Assert.Equal(("done", true, false, "mel-roformer-kim-vocals"), (done.Stage, done.Separate, done.KeepReverb, done.StemModel));
        Assert.Equal("?model=mel-roformer-kim-vocals&dereverb=true", _app.Stems.RequestUri!.Query);
        Assert.Equal(["song.flac"], _app.Mixer.Decoded);
        Assert.Equal(new MixInput("instrumental.wav", "converted.wav", "vocals_dry.wav", null), _app.Mixer.Input);
        Assert.Equal(FakeMixer.Flac, await client.GetByteArrayAsync($"/api/swaps/{done.Id}/audio"));
    }

    [Fact]
    public async Task A_failed_conversion_says_why_and_keeps_no_result()
    {
        _app.Voice.JobStatuses.Clear();
        _app.Voice.JobStatuses.Enqueue("FAILED");
        _app.Voice.JobError = "Zu wenig Speicher";
        var client = _app.CreateClient();

        await client.PostAsync("/api/swaps", Form(("voiceId", "v1")));
        var failed = await WaitForSwap(client, s => s.Finished);

        Assert.Equal(("failed", "Zu wenig Speicher"), (failed.Stage, failed.Message));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/swaps/{failed.Id}/audio")).StatusCode);
    }

    [Fact]
    public async Task A_missing_file_odd_settings_or_unknown_voice_are_refused()
    {
        var client = _app.CreateClient();

        var noFile = await client.PostAsync("/api/swaps", new MultipartFormDataContent { { new StringContent("v1"), "voiceId" } });
        var odd = await client.PostAsync("/api/swaps", Form(("voiceId", "v1"), ("diffusionSteps", "500")));
        var unknown = await client.PostAsync("/api/swaps", Form(("voiceId", "v9")));

        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.Contains("file", (await noFile.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<SwapState[]>("/api/swaps", TestApp.Json))!);
        Assert.False(Directory.Exists(Path.Combine(_app.Root, "swaps")) && Directory.EnumerateDirectories(Path.Combine(_app.Root, "swaps")).Any());
    }

    [Fact]
    public async Task A_whole_song_needs_the_separator_a_vocal_track_does_not()
    {
        _app.StemsBaseUrl = null;
        var client = _app.CreateClient();

        var whole = await client.PostAsync("/api/swaps", Form(("voiceId", "v1"), ("separate", "true")));
        var vocals = await client.PostAsync("/api/swaps", Form(("voiceId", "v1")));

        Assert.Equal(HttpStatusCode.NotImplemented, whole.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, vocals.StatusCode);
    }

    private static MultipartFormDataContent Form(params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Upload), "file", "Mein Gesang.wav" } };
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }
        return form;
    }

    private static async Task<SwapState> WaitForSwap(HttpClient client, Func<SwapState, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var swaps = await client.GetFromJsonAsync<SwapState[]>("/api/swaps", TestApp.Json);
            if (swaps?.FirstOrDefault() is { } swap && condition(swap))
            {
                return swap;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The swap never got there: {swaps?.FirstOrDefault()?.Stage}");
            }
            await Task.Delay(20);
        }
    }
}
