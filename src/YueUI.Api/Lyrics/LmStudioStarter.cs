using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Lyrics;

/// <summary>Starts LM Studio's server when it does not answer. Tests replace it.</summary>
public interface ILmStudioStarter
{
    /// <returns>False when LM Studio's command line tool is not installed.</returns>
    Task<bool> StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <c>lms daemon up</c> starts llmster, LM Studio without its window (or finds the app already running), and
/// <c>lms server start</c> its API. Both do nothing when already running, so the app need not be open.
/// </summary>
public sealed class LmsCli(IOptions<LyricsOptions> options, ILogger<LmsCli> logger) : ILmStudioStarter
{
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        var lms = options.Value.LmsPath;
        if (!File.Exists(lms))
        {
            return false;
        }
        await RunAsync(lms, ["daemon", "up"], cancellationToken);
        await RunAsync(lms, ["server", "start"], cancellationToken);
        return true;
    }

    private async Task RunAsync(string lms, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(lms) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {lms}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        logger.LogInformation("lms {Arguments} exited with {Code}: {Output}{Error}",
            string.Join(' ', arguments), process.ExitCode, (await output).Trim(), (await error).Trim());
    }
}
