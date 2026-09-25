using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using YueUI.Api.Data;
using YueUI.Api.Logic;
using YueUI.Api.Lyrics;
using YueUI.Api.Push;
using YueUI.Api.Worker;

namespace YueUI.Api.Tests;

/// <summary>
/// The app with a temporary song library and a scripted worker: tests read the commands the server sent and
/// answer with the events <c>yue2_worker.py</c> would write.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly bool _fakeWorker;

    public TestApp(bool fakeWorker = true)
    {
        _fakeWorker = fakeWorker;
        Root = Directory.CreateTempSubdirectory("yueui-").FullName;
        OutputDir = Path.Combine(Root, "songs");
        Directory.CreateDirectory(OutputDir);
    }

    public string Root { get; }

    public string OutputDir { get; }

    public FakeLauncher Launcher { get; } = new();

    public FakeStudio Studio { get; } = new();

    public FakeLmStudio LmStudio { get; } = new();

    public FakeLogic Logic { get; } = new();

    /// <summary>Where the fake yue-to-logic-pro is expected; set to null before the first request to switch the export off.</summary>
    public string? LogicBaseUrl { get; set; } = "http://logic.test/";

    public FakePushSender Push { get; } = new();

    public FakeWorker Worker => Launcher.Current ?? throw new InvalidOperationException("No worker was started.");

    /// <summary>Creates <c>run/songN</c> with the files the worker writes; <paramref name="files"/> overrides or adds.</summary>
    public string AddSong(string run, string song, string title = "Neon Night", string quality = "draft", params string[] files)
    {
        var directory = Path.Combine(OutputDir, run, song);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "request.json"), """{"style": "Dark synthwave", "lyrics": "[verse]\nLa la", "cot": "full", "seed": 42, "id": "song1"}""");
        File.WriteAllText(Path.Combine(directory, "result.json"), $$"""{"status": "complete", "audio_seconds": 187.5, "quality": "{{quality}}", "title": "{{title}}"}""");
        File.WriteAllBytes(Path.Combine(directory, "audio.flac"), [.. "fLaC"u8, .. new byte[96]]);
        File.WriteAllText(Path.Combine(directory, "score.abc"), "X:1\n");
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(directory, file), "");
        }
        return directory;
    }

    /// <summary>Where YuE Studio's installer puts SheetSage2's environment; an empty file is enough for the server.</summary>
    public void InstallSheetSage()
    {
        var python = Path.Combine(Root, "install", "sheetsage-env", "bin", "python");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "");
    }

    /// <summary>A transcription folder as the worker leaves it; without a score it stands for a failed one.</summary>
    public string AddTranscription(string name, string source = "My Song.mp3", string? score = "X:1\nV:Vocal\nE2G2|\n")
    {
        var directory = Path.Combine(OutputDir, "transcriptions", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "input.json"), $$"""{"source_name": "{{source}}", "task": "melody-vocal"}""");
        if (score is not null)
        {
            File.WriteAllText(Path.Combine(directory, "score.abc"), score);
            File.WriteAllText(Path.Combine(directory, "transcription.mid"), "MThd");
            File.WriteAllText(Path.Combine(directory, "transcription_manifest.json"), """{"status": "complete", "warnings": ["key uncertain"]}""");
        }
        else
        {
            File.WriteAllText(Path.Combine(directory, "failure.json"), """{"status": "failed"}""");
        }
        return directory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new YuePaths(Path.Combine(Root, "install"), OutputDir));
            services.AddSingleton<IStudioDetector>(Studio);
            services.AddSingleton<ILmStudioStarter>(LmStudio);
            // The model FakeLmStudio lists and a fixed context, whatever appsettings.json picks for the Mac.
            services.Configure<LyricsOptions>(options =>
            {
                options.Model = "google/gemma-4-e4b";
                options.ContextLength = 8192;
            });
            // A new handler each time: the factory disposes the ones it rotates out.
            services.AddHttpClient(LyricsWriter.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeLmStudio.Handler(LmStudio));
            services.Configure<LogicOptions>(options => options.BaseUrl = LogicBaseUrl);
            services.AddHttpClient(LogicEndpoints.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeLogic.Handler(Logic));

            services.Configure<PushOptions>(options => options.DataPath = Path.Combine(Root, "push.json"));
            services.AddSingleton<IPushSender>(Push);
            services.Configure<DataOptions>(options => options.Path = Path.Combine(Root, "yueui.db"));
            if (_fakeWorker)
            {
                services.AddSingleton<IWorkerLauncher>(Launcher);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // WebApplicationFactory gets here from Dispose and from DisposeAsync.
        if (disposing && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>Some effects follow the state that announces them, e.g. deleting an upload; wait for them.</summary>
    public static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition never held.");
            }
            await Task.Delay(20);
        }
    }

    /// <summary>The worker runs on its own thread; wait until the state shows what a test expects.</summary>
    public async Task<StatusSnapshot> WaitForStatus(HttpClient client, Func<StatusSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var status = (await client.GetFromJsonAsync<StatusSnapshot>("/api/status", Json))!;
            if (condition(status))
            {
                return status;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Status never matched: {JsonSerializer.Serialize(status, Json)}");
            }
            await Task.Delay(20);
        }
    }

    public string AudioPath(string run, string song) => Path.Combine(OutputDir, run, song, "audio.flac");
}

public sealed class FakeLauncher : IWorkerLauncher
{
    public int Launches { get; private set; }

    public FakeWorker? Current { get; private set; }

    public IWorkerConnection Launch()
    {
        Launches++;
        return Current = new FakeWorker();
    }
}

public sealed class FakeWorker : IWorkerConnection
{
    private readonly Channel<WorkerLine> _output = Channel.CreateUnbounded<WorkerLine>();
    private readonly Channel<JsonObject> _commands = Channel.CreateUnbounded<JsonObject>();

    public ChannelReader<WorkerLine> Output => _output.Reader;

    public bool Disposed { get; private set; }

    public Task SendAsync(string line, CancellationToken cancellationToken)
    {
        _commands.Writer.TryWrite(JsonNode.Parse(line)!.AsObject());
        return Task.CompletedTask;
    }

    public async Task<JsonObject> NextCommand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _commands.Reader.ReadAsync(timeout.Token);
    }

    public void Emit(object message) => _output.Writer.TryWrite(new WorkerLine(JsonSerializer.Serialize(message), false));

    public void Exit() => _output.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Exit();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeStudio : IStudioDetector
{
    public bool Running { get; set; }

    public bool IsRunning() => Running;
}

/// <summary>LM Studio's server as the lyrics writer sees it: models, chat completions and unload, all recorded.</summary>
public sealed class FakeLmStudio : ILmStudioStarter
{
    public bool Running { get; set; } = true;

    /// <summary>Whether <c>lms</c> is installed; starting it makes the server answer.</summary>
    public bool CanStart { get; set; } = true;

    public int Starts { get; private set; }

    public string Answer { get; set; } = "[Verse]\nLine one\n\n[Chorus]\nLine two";

    /// <summary>The whole answer instead of one built from <see cref="Answer"/>, e.g. with only reasoning.</summary>
    public object? RawAnswer { get; set; }

    /// <summary>An instance the user loaded in LM Studio themselves.</summary>
    public string? LoadedInstance { get; set; }

    /// <summary>LM Studio's guardrails refusing the load, with this message.</summary>
    public string? LoadRefusal { get; set; }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Holds the completion until a test lets it go, to look at the server meanwhile.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(string Path, JsonObject? Body)> Requests { get; } = [];

    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        Starts++;
        Running |= CanStart;
        return Task.FromResult(CanStart);
    }

    public sealed class Handler(FakeLmStudio lm) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!lm.Running)
            {
                throw new HttpRequestException("Connection refused");
            }
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            var path = request.RequestUri!.AbsolutePath;
            lock (lm.Requests)
            {
                lm.Requests.Add((path, body));
            }
            switch (path)
            {
                case "/v1/models":
                    return Json(HttpStatusCode.OK, new { data = new[] { new { id = "google/gemma-4-e4b" } } });
                case "/api/v1/models" when request.Method == HttpMethod.Get:
                    return Json(HttpStatusCode.OK, new
                    {
                        models = new object[]
                        {
                            new
                            {
                                type = "llm",
                                key = "google/gemma-4-e4b",
                                display_name = "Gemma 4 E4B",
                                size_bytes = 5_000_000_000L,
                                loaded_instances = lm.LoadedInstance is { } id ? new object[] { new { id } } : [],
                            },
                            new { type = "llm", key = "qwen/qwen3-8b", display_name = "Qwen3 8B", size_bytes = 5_500_000_000L, loaded_instances = Array.Empty<object>() },
                            new { type = "embedding", key = "text-embedding-nomic", display_name = "Nomic Embed", size_bytes = 80_000_000L, loaded_instances = Array.Empty<object>() },
                        },
                    });
                case "/api/v1/models/load":
                    return lm.LoadRefusal is { } refusal
                        ? Json(HttpStatusCode.InternalServerError, new { error = new { type = "model_load_failed", message = refusal } })
                        : Json(HttpStatusCode.OK, new
                        {
                            type = "llm",
                            instance_id = $"{body?["model"]}:1",
                            status = "loaded",
                            load_time_seconds = 2.5,
                            load_config = new { context_length = (int?)body?["context_length"] },
                        });
                case "/v1/chat/completions":
                    if (lm.Gate is { } gate)
                    {
                        await gate.Task.WaitAsync(cancellationToken);
                    }
                    return lm.Status == HttpStatusCode.OK
                        ? Json(HttpStatusCode.OK, lm.RawAnswer ?? new { choices = new[] { new { message = new { role = "assistant", content = lm.Answer } } } })
                        : Json(lm.Status, new { error = new { message = "Model not found" } });
                case "/api/v1/models/unload":
                    return Json(HttpStatusCode.OK, new { instance_id = (string?)body?["instance_id"] });
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status) { Content = JsonContent.Create(value) };
    }
}

/// <summary>yue-to-logic-pro's <c>POST /api/convert/logic</c>: records the form it gets and answers as told.</summary>
public sealed class FakeLogic
{
    public bool Running { get; set; } = true;

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>The body of a refusal (JSON), or of the ZIP.</summary>
    public string? Body { get; set; }

    public string? Diagnostics { get; set; }

    public byte[] Zip { get; set; } = [.. "PK"u8, 3, 4, 1, 2, 3];

    public Uri? RequestUri { get; private set; }

    /// <summary>The form fields by name; files as their bytes.</summary>
    public Dictionary<string, byte[]> Form { get; } = [];

    public Dictionary<string, string?> FileNames { get; } = [];

    public string Field(string name) => System.Text.Encoding.UTF8.GetString(Form[name]);

    public sealed class Handler(FakeLogic logic) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!logic.Running)
            {
                throw new HttpRequestException("Connection refused");
            }
            logic.RequestUri = request.RequestUri;
            // The in-memory handler gets the very content object the server built.
            foreach (var part in (MultipartFormDataContent)request.Content!)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                logic.Form[name] = await part.ReadAsByteArrayAsync(cancellationToken);
                logic.FileNames[name] = part.Headers.ContentDisposition.FileName?.Trim('"');
            }
            if (logic.Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(logic.Status)
                {
                    Content = new StringContent(logic.Body ?? "", System.Text.Encoding.UTF8, "application/json"),
                };
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(logic.Zip) };
            response.Content.Headers.ContentType = new("application/zip");
            if (logic.Diagnostics is { } diagnostics)
            {
                response.Headers.Add(LogicEndpoints.DiagnosticsHeader, diagnostics);
            }
            return response;
        }
    }
}

/// <summary>Records what would have gone to the push services; <see cref="Result"/> decides their answer.</summary>
public sealed class FakePushSender : IPushSender
{
    private readonly Channel<(PushSubscriptionEntry Subscription, JsonObject Payload)> _sent = Channel.CreateUnbounded<(PushSubscriptionEntry, JsonObject)>();

    public PushResult Result { get; set; } = PushResult.Delivered;

    public Task<PushResult> SendAsync(PushSubscriptionEntry subscription, string payload, CancellationToken cancellationToken)
    {
        _sent.Writer.TryWrite((subscription, JsonNode.Parse(payload)!.AsObject()));
        return Task.FromResult(Result);
    }

    public async Task<(PushSubscriptionEntry Subscription, JsonObject Payload)> Next()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _sent.Reader.ReadAsync(timeout.Token);
    }

    public bool TryNext(out (PushSubscriptionEntry Subscription, JsonObject Payload) sent) => _sent.Reader.TryRead(out sent);
}
