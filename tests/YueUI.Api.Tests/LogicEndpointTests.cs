using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using YueToLogic.Core.Conversion;

namespace YueUI.Api.Tests;

/// <summary>The export runs the real <c>YueToLogic.Core</c>; its own tests cover what goes into the project.</summary>
public sealed class LogicEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    /// <summary>The official YuE2 example (samples/score.abc); 1 047 273 samples at 48 kHz is its length.</summary>
    internal static readonly string SampleScore = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "score.abc"));

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task A_song_becomes_a_logic_project()
    {
        AddSong("song2", title: "Neon: Night?", Flac(48000, 1_047_273));
        var client = _app.CreateClient();

        var response = await client.GetAsync($"/api/songs/{Run}/song2/logic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Neon Night-song2.logicx.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("Neon Night-song2.logicx/", StringComparison.Ordinal) && e.Name == "ProjectData");
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".flac", StringComparison.Ordinal));
        Assert.False(response.Headers.Contains(LogicEndpoints.DiagnosticsHeader));
    }

    [Fact]
    public async Task Warnings_travel_in_a_header()
    {
        // Ten seconds of audio for a score of more than twenty: the project is built, with a warning about it.
        AddSong("song1", audio: Flac(48000, 480_000));

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = Assert.Single(response.Headers.GetValues(LogicEndpoints.DiagnosticsHeader));
        var diagnostics = System.Text.Json.JsonSerializer.Deserialize<List<Warning>>(header, TestApp.Json)!;
        Assert.Contains(diagnostics, d => d.Code == "YTL052" && d.Severity == "Warning");
    }

    [Theory]
    [InlineData("song9")]
    [InlineData("..")]
    public async Task Unknown_songs_are_not_found(string song)
    {
        AddSong("song1");

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/{song}/logic");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_song_without_its_score_is_not_found()
    {
        var directory = AddSong("song1");
        File.Delete(Path.Combine(directory, "score.abc"));

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Audio_logic_cannot_take_says_why()
    {
        AddSong("song1", audio: Flac(44100, 1_000_000));

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.StartsWith("This song cannot become a Logic project.", problem!.Detail);
        Assert.Contains("44100", problem.Detail);
    }

    [Fact]
    public async Task A_score_that_cannot_be_read_says_why()
    {
        var directory = AddSong("song1");
        File.WriteAllText(Path.Combine(directory, "score.abc"), "X:1\n");

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.StartsWith("This song cannot become a Logic project.", problem!.Detail);
    }

    [Fact]
    public async Task A_midi_file_comes_back_as_a_score()
    {
        // The example as MIDI, the way Logic would export it, reads back into the example.
        var midi = _app.Services.GetRequiredService<IScoreConverter>().Convert(SampleScore).Midi!;

        var response = await PostMidi(midi);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var score = await response.Content.ReadFromJsonAsync<MidiScore>(TestApp.Json);
        Assert.Equal(SampleScore.ReplaceLineEndings("\n"), score!.Abc.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_file_that_is_no_midi_says_why()
    {
        var response = await PostMidi([.. "MThd"u8]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.StartsWith("No score can be read from this MIDI file.", problem!.Detail);
    }

    [Fact]
    public async Task A_large_file_is_refused()
    {
        var response = await PostMidi(new byte[LogicEndpoints.MaxMidiBytes + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed record Warning(string Severity, string Code, string Message);

    private string AddSong(string song, string title = "Neon Night", byte[]? audio = null)
    {
        var directory = _app.AddSong(Run, song, title);
        File.WriteAllText(Path.Combine(directory, "score.abc"), SampleScore);
        File.WriteAllBytes(Path.Combine(directory, "audio.flac"), audio ?? Flac(48000, 1_047_273));
        return directory;
    }

    private Task<HttpResponseMessage> PostMidi(byte[] midi) =>
        _app.CreateClient().PostAsync("/api/midi/abc", new MultipartFormDataContent { { new ByteArrayContent(midi), "file", "song.mid" } });

    /// <summary>A FLAC header with its STREAMINFO block (stereo, 24 bit), followed by some bytes standing for the frames.</summary>
    internal static byte[] Flac(int sampleRate, long samples)
    {
        var data = new byte[42 + 256];
        "fLaC"u8.CopyTo(data);
        data[4] = 0x80; // last metadata block, type STREAMINFO
        data[7] = 34;
        var body = data.AsSpan(8, 34);
        body[10] = (byte)(sampleRate >> 12);
        body[11] = (byte)(sampleRate >> 4);
        body[12] = (byte)(((sampleRate & 0x0F) << 4) | (1 << 1) | (23 >> 4));
        body[13] = (byte)(((23 & 0x0F) << 4) | (int)((samples >> 32) & 0x0F));
        BinaryPrimitives.WriteUInt32BigEndian(body[14..], (uint)samples);
        return data;
    }
}
