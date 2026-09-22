using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new YuePaths(Path.Combine(Root, "install"), OutputDir));
            services.AddSingleton<IStudioDetector>(Studio);
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
