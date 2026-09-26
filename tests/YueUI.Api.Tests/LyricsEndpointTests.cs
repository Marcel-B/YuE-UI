using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

public sealed class LyricsEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _client;

    public LyricsEndpointTests() => _client = _app.CreateClient();

    [Fact]
    public async Task The_model_is_loaded_with_a_small_context_asked_with_YuE2s_rules_and_unloaded_after()
    {
        var draft = await Finished(new { keywords = "night train, leaving home", style = "English, melancholic folk, 80 BPM" });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", draft.Lyrics);

        var load = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load").Body!;
        Assert.Equal("google/gemma-4-e4b", (string?)load["model"]);
        Assert.Equal(8192, (int?)load["context_length"]);
        var completion = Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!;
        Assert.Equal("google/gemma-4-e4b:1", (string?)completion["model"]);
        Assert.Equal(60, (int?)completion["ttl"]);
        Assert.Equal(8192 - 1024, (int?)completion["max_tokens"]);
        var messages = completion["messages"]!.AsArray();
        Assert.Contains("[Verse]", (string?)messages[0]!["content"], StringComparison.Ordinal);
        Assert.Contains("Write in English", (string?)messages[0]!["content"], StringComparison.Ordinal);
        var user = (string)messages[1]!["content"]!;
        Assert.Contains("night train, leaving home", user, StringComparison.Ordinal);
        Assert.Contains("melancholic folk", user, StringComparison.Ordinal);

        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
        Assert.Equal("google/gemma-4-e4b:1", (string?)_app.LmStudio.Requests[^1].Body!["instance_id"]);
    }

    [Fact]
    public async Task German_lyrics_are_asked_for_in_German_with_the_tags_kept_in_English()
    {
        var draft = await Finished(new { keywords = "Nachtzug, Abschied", style = "German, synthwave", language = "german" });

        Assert.Equal("done", draft.Stage);
        var system = (string)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["messages"]![0]!["content"]!;
        Assert.Contains("Write in German", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Write in English", system, StringComparison.Ordinal);
        Assert.Contains("Keep these tags in English", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_picker_lists_LM_Studios_language_models_and_the_default()
    {
        var models = (await _client.GetFromJsonAsync<JsonObject>("/api/lyrics/models"))!;

        Assert.Equal("google/gemma-4-e4b", (string?)models["default"]);
        var list = models["models"]!.AsArray();
        Assert.Equal(["google/gemma-4-e4b", "qwen/qwen3-8b"], list.Select(m => (string?)m!["id"]));
        Assert.Equal("Qwen3 8B", (string?)list[1]!["name"]);
        Assert.Equal(5_500_000_000L, (long?)list[1]!["sizeBytes"]);
        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load");
    }

    [Fact]
    public async Task Listing_models_starts_LM_Studio_and_says_when_it_cannot()
    {
        _app.LmStudio.Running = false;
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/lyrics/models")).StatusCode);
        Assert.Equal(1, _app.LmStudio.Starts);

        _app.LmStudio.Running = false;
        _app.LmStudio.CanStart = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/api/lyrics/models")).StatusCode);
    }

    [Fact]
    public async Task A_chosen_model_is_loaded_asked_and_unloaded_instead_of_the_default()
    {
        var draft = await Finished(new { keywords = "summer", model = "qwen/qwen3-8b" });

        Assert.Equal("done", draft.Stage);
        Assert.Equal("qwen/qwen3-8b", (string?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load").Body!["model"]);
        Assert.Equal("qwen/qwen3-8b:1", (string?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["model"]);
        Assert.Equal("qwen/qwen3-8b:1", (string?)_app.LmStudio.Requests[^1].Body!["instance_id"]);
    }

    [Fact]
    public async Task An_overlong_model_id_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer", model = new string('x', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task An_unknown_language_is_refused()
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer", language = "klingon" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task A_model_already_loaded_in_LM_Studio_is_used_and_left_loaded()
    {
        _app.LmStudio.LoadedInstance = "google/gemma-4-e4b:2";

        await Draft();

        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path is "/api/v1/models/load" or "/api/v1/models/unload");
        Assert.Equal("google/gemma-4-e4b:2", (string?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["model"]);
    }

    [Fact]
    public async Task A_load_LM_Studio_refuses_for_lack_of_memory_is_explained_without_asking_the_model()
    {
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";

        var draft = await Finished(new { keywords = "summer" });

        Assert.Contains("insufficient system resources", draft.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_app.LmStudio.Requests, r => r.Path is "/v1/chat/completions" or "/api/v1/models/unload");
    }

    [Fact]
    public async Task A_refused_load_is_tried_again_with_half_the_context_down_to_the_minimum()
    {
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";

        var draft = await Finished(new { keywords = "summer" });

        Assert.Contains("not even with a context of 2048 tokens", draft.Message, StringComparison.Ordinal);
        Assert.Equal([8192, 4096, 2048], _app.LmStudio.Requests.Where(r => r.Path == "/api/v1/models/load").Select(r => (int)r.Body!["context_length"]!));
    }

    [Fact]
    public async Task A_smaller_context_LM_Studio_accepts_is_what_the_answer_gets()
    {
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";
        _app.LmStudio.RefusesContext = context => context > 4096;

        Assert.Equal("[Verse]\nLine one\n\n[Chorus]\nLine two", await Draft());

        Assert.Equal([8192, 4096], _app.LmStudio.Requests.Where(r => r.Path == "/api/v1/models/load").Select(r => (int)r.Body!["context_length"]!));
        Assert.Equal(4096 - 1024, (int?)Assert.Single(_app.LmStudio.Requests, r => r.Path == "/v1/chat/completions").Body!["max_tokens"]);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Fact]
    public async Task Other_loaded_models_are_unloaded_before_the_context_is_made_smaller()
    {
        _app.LmStudio.OtherLoadedInstance = "qwen/qwen3-8b";
        _app.LmStudio.LoadRefusal = "Model loading was stopped due to insufficient system resources.";
        _app.LmStudio.RefusesContext = _ => _app.LmStudio.OtherLoadedInstance is not null;

        await Draft();

        Assert.Null(_app.LmStudio.OtherLoadedInstance);
        Assert.Equal([8192, 8192], _app.LmStudio.Requests.Where(r => r.Path == "/api/v1/models/load").Select(r => (int)r.Body!["context_length"]!));
    }

    [Fact]
    public async Task Other_loaded_models_stay_when_the_model_loads()
    {
        _app.LmStudio.OtherLoadedInstance = "qwen/qwen3-8b";

        await Draft();

        Assert.Equal("qwen/qwen3-8b", _app.LmStudio.OtherLoadedInstance);
    }

    [Fact]
    public async Task A_load_refused_for_another_reason_is_not_tried_again()
    {
        _app.LmStudio.LoadRefusal = "Model not found";

        var draft = await Finished(new { keywords = "summer" });

        Assert.Contains("Model not found", draft.Message, StringComparison.Ordinal);
        Assert.Single(_app.LmStudio.Requests, r => r.Path == "/api/v1/models/load");
    }

    [Fact]
    public async Task A_model_that_only_thought_until_its_tokens_ran_out_says_so()
    {
        _app.LmStudio.RawAnswer = new
        {
            choices = new[] { new { finish_reason = "length", message = new { role = "assistant", content = "", reasoning_content = "Let me think about trains…" } } },
        };

        var draft = await Finished(new { keywords = "summer" });

        Assert.Equal("failed", draft.Stage);
        Assert.Contains("thinking", draft.Message, StringComparison.Ordinal);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Fact]
    public async Task What_models_add_around_the_lyrics_is_removed()
    {
        _app.LmStudio.Answer = "<think>\nThe user wants a sad song.\n</think>\n```\n**Verse 1:**\nRain on the window\n\n\n\nchorus\nHold on  \n```";

        var draft = await Draft();

        Assert.Equal("[Verse 1]\nRain on the window\n\n[chorus]\nHold on", draft);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_started_with_lms()
    {
        _app.LmStudio.Running = false;

        Assert.Equal("done", (await Finished(new { keywords = "summer" })).Stage);
        Assert.Equal(1, _app.LmStudio.Starts);
    }

    [Fact]
    public async Task Without_lms_an_unreachable_server_is_explained()
    {
        _app.LmStudio.Running = false;
        _app.LmStudio.CanStart = false;

        var draft = await Finished(new { keywords = "summer" });

        Assert.Equal("failed", draft.Stage);
        Assert.Contains("does not answer", draft.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_of_LM_Studio_is_passed_on_and_the_model_still_unloaded()
    {
        _app.LmStudio.Status = HttpStatusCode.NotFound;

        var draft = await Finished(new { keywords = "summer" });

        Assert.Equal("failed", draft.Stage);
        Assert.Contains("Model not found", draft.Message, StringComparison.Ordinal);
        Assert.Equal("/api/v1/models/unload", _app.LmStudio.Requests[^1].Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Keywords_are_required(string keywords)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
    }

    [Fact]
    public async Task No_draft_while_YuE2_generates()
    {
        await StartSong();

        var response = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_app.LmStudio.Requests);
        Assert.False(_app.Worker.Disposed);
    }

    [Fact]
    public async Task An_idle_YuE_worker_is_stopped_to_free_its_memory()
    {
        await StartSong();
        _app.Worker.Emit(new { @event = "stage", path = _app.AudioPath("20260922-101500-Neon-Night", "song1"), stage = "ready", detail = "" });
        await _app.WaitForStatus(_client, s => !s.Worker.Busy);

        await Finished(new { keywords = "summer" });

        Assert.True(_app.Worker.Disposed);
    }

    [Fact]
    public async Task While_lyrics_are_written_no_song_starts_and_no_second_draft()
    {
        _app.LmStudio.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drafting = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "summer" });
        Assert.Equal(HttpStatusCode.Accepted, drafting.StatusCode);
        var id = (await drafting.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        await WaitFor(() => _app.LmStudio.Requests.Any(r => r.Path == "/v1/chat/completions"));
        Assert.Equal("writing", (await _app.WaitForStatus(_client, s => s.Lyrics?.Id == id)).Lyrics!.Stage);

        var song = await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa" });
        var second = await _client.PostAsJsonAsync("/api/lyrics", new { keywords = "winter" });

        Assert.Equal(HttpStatusCode.Conflict, song.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(0, _app.Launcher.Launches);

        _app.LmStudio.Gate.SetResult();
        await _app.WaitForStatus(_client, s => s.Lyrics is { Stage: "done" });
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa" })).StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    private async Task<string> Draft()
    {
        var draft = await Finished(new { keywords = "rain" });
        Assert.Equal("done", draft.Stage);
        return draft.Lyrics!;
    }

    /// <summary>Starts a draft (202) and waits for its result in the status, where the browsers get it.</summary>
    private async Task<LyricsState> Finished(object request)
    {
        var response = await _client.PostAsJsonAsync("/api/lyrics", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<LyricsState>(TestApp.Json))!.Id;
        return (await _app.WaitForStatus(_client, s => s.Lyrics is { Finished: true } lyrics && lyrics.Id == id)).Lyrics!;
    }

    /// <summary>A generate command and one queued song, as in WorkerEndpointTests.</summary>
    private async Task StartSong()
    {
        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsJsonAsync("/api/generate", new { style = "Pop", lyrics = "[verse]\nLa" })).StatusCode);
        await _app.Worker.NextCommand();
        _app.Worker.Emit(new { @event = "ready" });
        _app.Worker.Emit(new
        {
            @event = "started",
            job = "20260922-101500-Neon-Night",
            title = "Neon Night",
            songs = new[] { new { index = 1, seed = 1, path = _app.AudioPath("20260922-101500-Neon-Night", "song1"), priority = 1 } },
        });
        await _app.WaitForStatus(_client, s => s.Worker.Busy);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }
            await Task.Delay(20);
        }
    }
}
