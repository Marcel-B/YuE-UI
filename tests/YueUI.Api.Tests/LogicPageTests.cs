using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace YueUI.Api.Tests;

/// <summary>
/// The Logic page's server side: instruments, presets, the import from yue-to-logic-pro, and conversions with options.
/// The answers are read as JSON nodes, since they spell enums by name (YueToLogic.Core's convention), not camelCase.
/// </summary>
public sealed class LogicPageTests : IDisposable
{
    private const string Run = "20260921-165850-Neon-Night";

    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public LogicPageTests() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task An_instrument_is_created_listed_changed_and_deleted()
    {
        var created = await _client.PostAsJsonAsync("/api/instruments", new { name = " Mother32 ", port = "MIDI4x4 Midi Out 1", channel = 12 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var instrument = await Json(created);
        var id = (long)instrument["id"]!;
        Assert.Equal("Mother32", (string?)instrument["name"]);
        Assert.Equal("Synth", (string?)instrument["kind"]);
        Assert.Null(instrument["drums"]);

        var changed = await _client.PutAsJsonAsync($"/api/instruments/{id}", new { name = "Mother-32", port = "Scarlett 8i6 USB", channel = 3 });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var listed = (await Json(await _client.GetAsync("/api/instruments"))).AsArray();
        Assert.Equal("Scarlett 8i6 USB", (string?)Assert.Single(listed)!["port"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/instruments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/instruments/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_drum_machine_keeps_the_notes_of_its_drums()
    {
        var created = await _client.PostAsJsonAsync("/api/instruments", new
        {
            name = "DrumBrute Impact",
            port = "MIDI4x4 Midi Out 2",
            channel = 8,
            kind = "DrumMachine",
            drums = new { kick = 36, snare = 37, closedHiHat = 44, openHiHat = 45, crash = 51, clap = 39 },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var listed = Assert.Single((await Json(await _client.GetAsync("/api/instruments"))).AsArray())!;
        Assert.Equal("DrumMachine", (string?)listed["kind"]);
        Assert.Equal(44, (int)listed["drums"]!["closedHiHat"]!);
    }

    [Fact]
    public async Task Invalid_and_duplicate_instruments_are_refused()
    {
        await _client.PostAsJsonAsync("/api/instruments", new { name = "Mother32", port = "Out 1", channel = 12 });

        var invalid = await _client.PostAsJsonAsync("/api/instruments", new { name = "", port = "Out 1", channel = 17 });
        var taken = await _client.PostAsJsonAsync("/api/instruments", new { name = "mother32", port = "Out 2", channel = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
    }

    [Fact]
    public async Task A_track_is_assigned_and_loses_its_instrument_with_it()
    {
        var id = (long)(await Json(await _client.PostAsJsonAsync("/api/instruments", new { name = "Mother32", port = "Out 1", channel = 12 })))["id"]!;

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PutAsJsonAsync("/api/instruments/assignments/Bass", new { instrumentId = id })).StatusCode);
        Assert.Equal(id, (long)(await Json(await _client.GetAsync("/api/instruments/assignments")))["Bass"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/instruments/assignments/Vocal", new { instrumentId = id + 1 })).StatusCode);

        await _client.DeleteAsync($"/api/instruments/{id}");

        Assert.Empty((await Json(await _client.GetAsync("/api/instruments/assignments"))).AsObject());
    }

    [Fact]
    public async Task A_preset_is_saved_replaced_by_name_and_deleted()
    {
        var created = await _client.PutAsJsonAsync("/api/logic/presets/Live", new { form = new { bass = "Walking" } });
        var replaced = await _client.PutAsJsonAsync("/api/logic/presets/LIVE", new { form = new { bass = "Octaves" } });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var preset = Assert.Single((await Json(await _client.GetAsync("/api/logic/presets"))).AsArray())!;
        Assert.Equal("LIVE", (string?)preset["name"]);
        Assert.Equal("Octaves", (string?)preset["form"]!["bass"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/logic/presets/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/logic/presets/live")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync("/api/logic/presets/Live", new { form = "text" })).StatusCode);
    }

    [Fact]
    public async Task What_yue_to_logic_pro_kept_is_imported_once()
    {
        var import = new
        {
            instruments = new object[]
            {
                new { id = 7, name = "Mother32", port = "Out 1", channel = 12, kind = "Synth", drums = (object?)null },
                new { id = 9, name = "TR-8", port = "Out 3", channel = 10, kind = "DrumMachine", drums = (object?)new { kick = 36, snare = 38, closedHiHat = 42, openHiHat = 46, crash = 49, clap = 39 } },
                new { id = 11, name = "", port = "Out 4", channel = 1, kind = "Synth", drums = (object?)null },
            },
            assignments = new Dictionary<string, long> { ["Bass"] = 7, ["Drums"] = 9, ["Ins"] = 11 },
            presets = new object[] { new { name = "Live", form = new { bass = "Walking" } } },
        };

        var first = await Json(await _client.PostAsJsonAsync("/api/logic/import", import));
        var second = await Json(await _client.PostAsJsonAsync("/api/logic/import", import));

        Assert.Equal(2, (int)first["instruments"]!);
        Assert.Equal(2, (int)first["assignments"]!);
        Assert.Equal(1, (int)first["presets"]!);
        Assert.Single(first["skipped"]!.AsArray());
        Assert.Equal(2, (int)second["instruments"]!);
        var instruments = (await Json(await _client.GetAsync("/api/instruments"))).AsArray();
        Assert.Equal(2, instruments.Count);
        var assignments = await Json(await _client.GetAsync("/api/instruments/assignments"));
        var trId = (long)instruments.Single(i => (string?)i!["name"] == "TR-8")!["id"]!;
        Assert.Equal(trId, (long)assignments["Drums"]!);
        Assert.Single((await Json(await _client.GetAsync("/api/logic/presets"))).AsArray());
    }

    [Fact]
    public async Task A_library_song_is_converted_with_the_options()
    {
        AddSong("song1");

        var response = await _client.PostAsync("/api/logic/convert", new MultipartFormDataContent
        {
            { new StringContent($"{Run}/song1"), "song" },
            { new StringContent("""{"arrangement":{"bass":{"pattern":"Walking","octaveShift":0}}}"""), "options" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Json(response);
        Assert.True((bool)result["success"]!);
        Assert.False(string.IsNullOrEmpty((string?)result["midi"]));
        var voices = result["score"]!["voices"]!.AsArray();
        Assert.Contains(voices, v => (string?)v!["id"] == "Bass" && (string?)v["kind"] == "Bass");
    }

    [Fact]
    public async Task An_uploaded_score_that_cannot_be_read_comes_back_with_its_diagnostics()
    {
        var response = await _client.PostAsync("/api/logic/convert", new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("X:1\n")), "file", "score.abc" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var result = await Json(response);
        Assert.False((bool)result["success"]!);
        Assert.Contains(result["diagnostics"]!.AsArray(), d => (string?)d!["severity"] == "Error");
    }

    [Theory]
    [InlineData("song9")]
    [InlineData("../song1")]
    public async Task Unknown_songs_are_not_converted(string song)
    {
        AddSong("song1");

        var response = await _client.PostAsync("/api/logic/convert", new MultipartFormDataContent { { new StringContent($"{Run}/{song}"), "song" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_options_are_refused()
    {
        AddSong("song1");

        var response = await _client.PostAsync("/api/logic/convert", new MultipartFormDataContent
        {
            { new StringContent($"{Run}/song1"), "song" },
            { new StringContent("{not json"), "options" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_library_song_becomes_musicxml_with_its_options_and_name()
    {
        AddSong("song1");

        var response = await _client.PostAsync("/api/logic/musicxml", new MultipartFormDataContent
        {
            { new StringContent($"{Run}/song1"), "song" },
            { new StringContent("""{"arrangement":{"bass":{"pattern":"Walking","octaveShift":0}}}"""), "options" },
            { new StringContent("Neon Night"), "name" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.recordare.musicxml+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Neon Night.musicxml", response.Content.Headers.ContentDisposition!.FileNameStar);
        var document = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("score-partwise", document.Root!.Name.LocalName);
        Assert.Contains("Bass", document.Descendants("part-abbreviation").Select(p => p.Value));
    }

    [Fact]
    public async Task A_score_that_cannot_be_read_gives_no_musicxml()
    {
        var response = await _client.PostAsync("/api/logic/musicxml", new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("X:1\n")), "file", "score.abc" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False((bool)(await Json(response))["success"]!);
    }

    [Fact]
    public async Task A_library_song_is_exported_with_its_instruments_and_name()
    {
        AddSong("song1");

        var response = await _client.PostAsync("/api/logic/export", new MultipartFormDataContent
        {
            { new StringContent($"{Run}/song1"), "song" },
            { new StringContent("""{"arrangement":{"bass":{"pattern":"Eighths","octaveShift":0}}}"""), "options" },
            { new StringContent("Neon/Take 2"), "name" },
            { new StringContent("true"), "splitSections" },
            { new StringContent("""{"Bass":{"name":"Mother32","port":"MIDI4x4 Midi Out 1","channel":12}}"""), "instruments" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NeonTake 2.logicx.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("NeonTake 2.logicx/", StringComparison.Ordinal) && e.Name == "ProjectData");
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".flac", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_uploaded_score_is_exported_without_audio()
    {
        var response = await _client.PostAsync("/api/logic/export", new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(LogicEndpointTests.SampleScore)), "file", "My Song.abc" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("My Song.logicx.zip", response.Content.Headers.ContentDisposition!.FileNameStar);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".flac", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_export_answers_with_the_diagnostics()
    {
        AddSong("song1", LogicEndpointTests.Flac(44100, 1_000_000));

        var response = await _client.PostAsync("/api/logic/export", new MultipartFormDataContent { { new StringContent($"{Run}/song1"), "song" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var result = await Json(response);
        Assert.Contains(result["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "YTL051");
    }

    [Fact]
    public async Task Invalid_instruments_are_refused()
    {
        AddSong("song1");

        var response = await _client.PostAsync("/api/logic/export", new MultipartFormDataContent
        {
            { new StringContent($"{Run}/song1"), "song" },
            { new StringContent("""{"Bass":{"name":"Mother32","port":"Out 1","channel":17}}"""), "instruments" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("between 1 and 16", problem!.Detail);
    }

    private void AddSong(string song, byte[]? audio = null)
    {
        var directory = _app.AddSong(Run, song);
        File.WriteAllText(Path.Combine(directory, "score.abc"), LogicEndpointTests.SampleScore);
        File.WriteAllBytes(Path.Combine(directory, "audio.flac"), audio ?? LogicEndpointTests.Flac(48000, 1_047_273));
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
