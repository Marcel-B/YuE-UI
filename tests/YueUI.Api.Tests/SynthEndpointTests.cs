using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace YueUI.Api.Tests;

/// <summary>The Logic page's browser synthesizer: named sounds and the sound each track plays, kept on the server.</summary>
public sealed class SynthEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public SynthEndpointTests() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task A_sound_is_saved_replaced_by_name_and_deleted()
    {
        var created = await _client.PutAsJsonAsync("/api/logic/synths/presets/Fat Bass", new { patch = new { cutoff = 400 } });
        var replaced = await _client.PutAsJsonAsync("/api/logic/synths/presets/FAT BASS", new { patch = new { cutoff = 800 } });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var preset = Assert.Single((await Json(await _client.GetAsync("/api/logic/synths/presets"))).AsArray())!;
        Assert.Equal("FAT BASS", (string?)preset["name"]);
        Assert.Equal(800, (int)preset["patch"]!["cutoff"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/logic/synths/presets/fat bass")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/logic/synths/presets/fat bass")).StatusCode);
    }

    [Fact]
    public async Task A_track_keeps_its_sound_until_it_is_given_back_the_default()
    {
        await _client.PutAsJsonAsync("/api/logic/synths/presets/Pad", new { patch = new { cutoff = 1200 } });
        var set = await _client.PutAsJsonAsync("/api/logic/synths/tracks/Chords", new { patch = new { cutoff = 1500 }, preset = "Pad" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        await _client.PutAsJsonAsync("/api/logic/synths/tracks/chords", new { patch = new { cutoff = 1600 }, preset = "Pad" });

        // Deleting the preset leaves the track's copy alone.
        await _client.DeleteAsync("/api/logic/synths/presets/Pad");
        var track = Assert.Single((await Json(await _client.GetAsync("/api/logic/synths/tracks"))).AsArray())!;
        Assert.Equal("Chords", (string?)track["track"]);
        Assert.Equal(1600, (int)track["patch"]!["cutoff"]!);
        Assert.Equal("Pad", (string?)track["preset"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/logic/synths/tracks/Chords")).StatusCode);
        Assert.Empty((await Json(await _client.GetAsync("/api/logic/synths/tracks"))).AsArray());
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/logic/synths/tracks/Chords")).StatusCode);
    }

    [Theory]
    [InlineData("/api/logic/synths/presets/Pad")]
    [InlineData("/api/logic/synths/tracks/Bass")]
    public async Task A_patch_must_be_an_object(string path)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync(path, new { patch = "text" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync(path, new { })).StatusCode);
    }

    [Fact]
    public async Task The_mixer_is_empty_until_saved_and_then_replaced_whole()
    {
        var empty = await Json(await _client.GetAsync("/api/logic/synths/mixer"));
        Assert.Null(empty["settings"]);

        await _client.PutAsJsonAsync("/api/logic/synths/mixer", new { settings = new { master = 0.8, tracks = new { VOCAL = new { volume = 0.5, pan = -0.25 } } } });
        var saved = await _client.PutAsJsonAsync("/api/logic/synths/mixer", new { settings = new { master = 0.6 } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var mixer = await Json(await _client.GetAsync("/api/logic/synths/mixer"));
        Assert.Equal(0.6, (double)mixer["settings"]!["master"]!);
        Assert.Null(mixer["settings"]!["tracks"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/logic/synths/mixer", new { settings = 1 })).StatusCode);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
