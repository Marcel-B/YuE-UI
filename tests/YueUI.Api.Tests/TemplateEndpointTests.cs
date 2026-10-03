using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace YueUI.Api.Tests;

/// <summary>Prompt templates: a style and parameters kept under a name for the create page.</summary>
public sealed class TemplateEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public TemplateEndpointTests() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task A_template_is_saved_overwritten_renamed_and_deleted()
    {
        var response = await _client.PostAsJsonAsync("/api/templates", new
        {
            name = "  Dark Synthwave  ",
            settings = new { style = "German, dark synthwave", seed = "42", semanticSampling = new { temperature = 0.9 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Json(response);
        var id = (long)created["id"]!;
        Assert.Equal("Dark Synthwave", (string?)created["name"]);
        Assert.Equal(0.9, (double)created["settings"]!["semanticSampling"]!["temperature"]!);

        // Overwriting keeps the name, renaming keeps the settings.
        var overwritten = await _client.PutAsJsonAsync($"/api/templates/{id}", new { settings = new { style = "German, darker synthwave" } });
        Assert.Equal(HttpStatusCode.OK, overwritten.StatusCode);
        var renamed = await _client.PutAsJsonAsync($"/api/templates/{id}", new { name = "Nachtfahrt" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var template = Assert.Single((await Json(await _client.GetAsync("/api/templates"))).AsArray())!;
        Assert.Equal("Nachtfahrt", (string?)template["name"]);
        Assert.Equal("German, darker synthwave", (string?)template["settings"]!["style"]);
        Assert.Null(template["settings"]!["seed"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/templates/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/templates/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync($"/api/templates/{id}", new { name = "Weg" })).StatusCode);
        Assert.Empty((await Json(await _client.GetAsync("/api/templates"))).AsArray());
    }

    [Fact]
    public async Task Names_are_unique_regardless_of_case_and_listed_alphabetically()
    {
        await _client.PostAsJsonAsync("/api/templates", new { name = "Piano Pop", settings = new { style = "a" } });
        var other = await Json(await _client.PostAsJsonAsync("/api/templates", new { name = "ambient", settings = new { style = "b" } }));

        var duplicate = await _client.PostAsJsonAsync("/api/templates", new { name = "PIANO POP", settings = new { style = "c" } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var renamedOnto = await _client.PutAsJsonAsync($"/api/templates/{(long)other["id"]!}", new { name = "piano pop" });
        Assert.Equal(HttpStatusCode.Conflict, renamedOnto.StatusCode);
        // A template may change the case of its own name.
        var recased = await _client.PutAsJsonAsync($"/api/templates/{(long)other["id"]!}", new { name = "Ambient" });
        Assert.Equal(HttpStatusCode.OK, recased.StatusCode);

        var names = (await Json(await _client.GetAsync("/api/templates"))).AsArray().Select(t => (string?)t!["name"]);
        Assert.Equal(["Ambient", "Piano Pop"], names);
    }

    [Fact]
    public async Task A_template_needs_a_name_and_an_object_of_settings()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/templates", new { name = " ", settings = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/templates", new { name = "A" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/templates", new { name = "A", settings = "style" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/templates", new { name = new string('x', 101), settings = new { } })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/templates", new { name = "A", settings = new { style = new string('x', 20_000) } })).StatusCode);
    }

    [Fact]
    public async Task Templates_live_in_the_database_and_outlast_a_restart()
    {
        await _client.PostAsJsonAsync("/api/templates", new { name = "Jazz-Funk", settings = new { style = "English, jazz-funk" } });

        using var restarted = new TestApp();
        File.Copy(Path.Combine(_app.Root, "yueui.db"), Path.Combine(restarted.Root, "yueui.db"));
        var template = Assert.Single((await Json(await restarted.CreateClient().GetAsync("/api/templates"))).AsArray())!;
        Assert.Equal("English, jazz-funk", (string?)template["settings"]!["style"]);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
