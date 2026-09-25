using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace YueUI.Api.Tests;

public sealed class LogicEndpointTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Song_goes_to_yue_to_logic_and_its_project_comes_back()
    {
        _app.AddSong(Run, "song2", title: "Neon: Night?");
        _app.Logic.Diagnostics = """[{"severity":"Warning","code":"YTL053","message":"No template track for Guide."}]""";
        var client = _app.CreateClient();

        var response = await client.GetAsync($"/api/songs/{Run}/song2/logic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Neon Night-song2.logicx.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(_app.Logic.Zip, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(_app.Logic.Diagnostics, Assert.Single(response.Headers.GetValues(LogicEndpoints.DiagnosticsHeader)));

        Assert.Equal("http://logic.test/api/convert/logic", _app.Logic.RequestUri!.ToString());
        Assert.Equal(File.ReadAllBytes(_app.AudioPath(Run, "song2")), _app.Logic.Form["audio"]);
        Assert.Equal("X:1\n", _app.Logic.Field("file"));
        Assert.Equal("score.abc", _app.Logic.FileNames["file"]);
        Assert.Equal("audio.flac", _app.Logic.FileNames["audio"]);
        Assert.Equal("Neon Night-song2", _app.Logic.Field("name"));
        Assert.Equal("false", _app.Logic.Field("splitSections"));
        // The tempo is fitted to the length the worker measured (result.json's audio_seconds).
        Assert.Equal(187.5, (double)JsonNode.Parse(_app.Logic.Field("options"))!["fitTempo"]!["audioSeconds"]!);
    }

    [Fact]
    public async Task Without_a_server_the_export_is_off()
    {
        _app.LogicBaseUrl = null;
        _app.AddSong(Run, "song1");
        var client = _app.CreateClient();

        var info = await client.GetFromJsonAsync<LogicExportInfo>("/api/logic", TestApp.Json);
        var response = await client.GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.False(info!.Configured);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Empty(_app.Logic.Form);
    }

    [Fact]
    public async Task A_configured_server_is_announced()
    {
        var info = await _app.CreateClient().GetFromJsonAsync<LogicExportInfo>("/api/logic", TestApp.Json);

        Assert.True(info!.Configured);
    }

    [Theory]
    [InlineData("song9")]
    [InlineData("..")]
    public async Task Unknown_songs_are_not_found(string song)
    {
        _app.AddSong(Run, "song1");

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/{song}/logic");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_app.Logic.Form);
    }

    [Fact]
    public async Task A_song_without_its_score_is_not_found()
    {
        var directory = _app.AddSong(Run, "song1");
        File.Delete(Path.Combine(directory, "score.abc"));

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_refused_song_says_why()
    {
        _app.AddSong(Run, "song1");
        _app.Logic.Status = HttpStatusCode.UnprocessableEntity;
        _app.Logic.Body = """
            {"success":false,"diagnostics":[
              {"severity":"Warning","code":"YTL010","message":"Unknown field."},
              {"severity":"Error","code":"YTL052","message":"The mix has 44100 Hz; the Logic project needs 48000 Hz."}]}
            """;

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.EndsWith("The mix has 44100 Hz; the Logic project needs 48000 Hz.", problem!.Detail);
        Assert.DoesNotContain("Unknown field", problem.Detail);
    }

    [Fact]
    public async Task Another_refusal_is_a_bad_gateway()
    {
        _app.AddSong(Run, "song1");
        _app.Logic.Status = HttpStatusCode.BadRequest;
        _app.Logic.Body = """{"title":"Missing score file"}""";

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("yue-to-logic-pro answered 400. Missing score file", problem!.Detail);
    }

    [Fact]
    public async Task An_unreachable_server_is_a_bad_gateway()
    {
        _app.AddSong(Run, "song1");
        _app.Logic.Running = false;

        var response = await _app.CreateClient().GetAsync($"/api/songs/{Run}/song1/logic");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
