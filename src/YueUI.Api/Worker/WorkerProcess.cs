using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace YueUI.Api.Worker;

/// <summary>One line the worker wrote: a JSON event on stdout, or anything on stderr (tracebacks, warnings).</summary>
public readonly record struct WorkerLine(string Text, bool IsError);

/// <summary>A running worker: JSON lines in, lines out. <see cref="Output"/> completes when the process has ended.</summary>
public interface IWorkerConnection : IAsyncDisposable
{
    ChannelReader<WorkerLine> Output { get; }

    Task SendAsync(string line, CancellationToken cancellationToken);
}

/// <summary>Starts a worker. Tests replace it with a fake that scripts the worker's events.</summary>
public interface IWorkerLauncher
{
    /// <exception cref="WorkerUnavailableException">YuE Studio is not installed where the configuration says.</exception>
    IWorkerConnection Launch();
}

public sealed class WorkerUnavailableException(string message) : Exception(message);

/// <summary>Runs YuE Studio's own <c>yue2_worker.py</c> with the Python environment the app installed.</summary>
public sealed class PythonWorkerLauncher(YuePaths paths, ILogger<PythonWorkerLauncher> logger) : IWorkerLauncher
{
    public IWorkerConnection Launch()
    {
        if (!File.Exists(paths.Python) || !File.Exists(paths.WorkerScript))
        {
            throw new WorkerUnavailableException(
                $"YuE Studio was not found under '{paths.InstallRoot}' (expected env/bin/python and src/tools/yue2_worker.py). Set Yue:InstallRoot.");
        }

        var start = new ProcessStartInfo(paths.Python)
        {
            WorkingDirectory = paths.SourceRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(paths.WorkerScript);
        foreach (var (name, value) in paths.WorkerEnvironment)
        {
            start.Environment[name] = value;
        }

        Directory.CreateDirectory(paths.OutputDir);
        var process = Process.Start(start) ?? throw new WorkerUnavailableException($"Could not start {paths.Python}.");
        logger.LogInformation("Started YuE worker (pid {Pid})", process.Id);
        return new PythonWorkerConnection(process, logger);
    }

    private sealed class PythonWorkerConnection : IWorkerConnection
    {
        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly Channel<WorkerLine> _output = Channel.CreateUnbounded<WorkerLine>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim _stdin = new(1, 1);
        private readonly Task _pump;

        public PythonWorkerConnection(Process process, ILogger logger)
        {
            _process = process;
            _logger = logger;
            _pump = PumpAsync();
        }

        public ChannelReader<WorkerLine> Output => _output.Reader;

        public async Task SendAsync(string line, CancellationToken cancellationToken)
        {
            await _stdin.WaitAsync(cancellationToken);
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                _stdin.Release();
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                await Task.WhenAll(ReadAsync(_process.StandardOutput, isError: false), ReadAsync(_process.StandardError, isError: true));
                await _process.WaitForExitAsync();
                _logger.LogInformation("YuE worker exited with code {Code}", _process.ExitCode);
                _output.Writer.TryWrite(new WorkerLine($"Worker exited with code {_process.ExitCode}", _process.ExitCode != 0));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Reading the YuE worker failed");
            }
            finally
            {
                _output.Writer.TryComplete();
            }
        }

        private async Task ReadAsync(StreamReader reader, bool isError)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _output.Writer.TryWrite(new WorkerLine(line, isError));
            }
        }

        /// <summary>Asks the worker to quit (it cancels its songs first) and kills it if it does not within a few seconds.</summary>
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try
                {
                    await SendAsync("""{"cmd": "quit"}""", CancellationToken.None);
                    _process.StandardInput.Close();
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _process.WaitForExitAsync(grace.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
                {
                    _logger.LogWarning("YuE worker did not quit in time; killing it");
                    _process.Kill(entireProcessTree: true);
                }
            }
            await _pump;
            _process.Dispose();
        }
    }
}
