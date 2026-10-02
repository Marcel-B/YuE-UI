using System.Diagnostics;

namespace YueUI.Api.Voices;

/// <param name="Output">Standard output, whole.</param>
/// <param name="Tail">The last lines of both outputs, to say why it failed.</param>
public sealed record ToolResult(int ExitCode, string Output, IReadOnlyList<string> Tail);

/// <summary>Runs a command line tool (ffmpeg, mlx-audio-separator, Seed-VC's Python) and waits for it.</summary>
public static class ToolProcess
{
    private const int TailLines = 40;

    /// <param name="line">Called for every line of standard output as it comes, for progress.</param>
    /// <exception cref="VoiceServiceException">It could not be started or ran longer than <paramref name="timeout"/>.</exception>
    public static async Task<ToolResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        Action<string>? line = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new VoiceServiceException($"Could not start {executable}.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new VoiceServiceException($"Could not start {executable}: {exception.Message}", System.Net.HttpStatusCode.NotImplemented);
        }
        using (process)
        {
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var tail = new Queue<string>();
            var output = new System.Text.StringBuilder();
            void Keep(string read)
            {
                lock (tail)
                {
                    tail.Enqueue(read);
                    while (tail.Count > TailLines)
                    {
                        tail.Dequeue();
                    }
                }
            }
            try
            {
                var errors = PumpAsync(process.StandardError, Keep, limit.Token);
                await PumpAsync(process.StandardOutput, read =>
                {
                    output.AppendLine(read);
                    Keep(read);
                    line?.Invoke(read);
                }, limit.Token);
                await errors;
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new VoiceServiceException($"{Path.GetFileName(executable)} took longer than {timeout.TotalMinutes:0} minutes.", System.Net.HttpStatusCode.GatewayTimeout);
            }
            lock (tail)
            {
                return new ToolResult(process.ExitCode, output.ToString(), [.. tail]);
            }
        }
    }

    /// <summary>The last line that says something, as the reason a tool failed.</summary>
    public static string Reason(ToolResult result, string fallback)
    {
        var lines = result.Tail.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.LastOrDefault(l => l.Contains("Error", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? fallback;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> line, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } read)
        {
            line(read);
        }
    }
}
