using System.Diagnostics;

namespace YueUI.Api.Share;

/// <summary>Makes the small AAC a song is shared as. Tests replace it.</summary>
public interface IAudioEncoder
{
    /// <summary>Writes <paramref name="flac"/> as AAC in an MPEG-4 container (<c>.m4a</c>) to <paramref name="m4a"/>.</summary>
    /// <returns>False when this machine has no encoder.</returns>
    Task<bool> EncodeAsync(string flac, string m4a, CancellationToken cancellationToken);
}

/// <summary>
/// <c>afconvert</c> comes with macOS and reads FLAC, so the Mac needs nothing installed; <c>ffmpeg</c> is the
/// fallback for a development machine elsewhere. 128 kbit/s makes a three-minute song about 3 MB instead of the
/// FLAC's 30, small enough for any messenger, and AAC in <c>.m4a</c> plays everywhere, iOS included.
/// </summary>
public sealed class AacEncoder(ILogger<AacEncoder> logger) : IAudioEncoder
{
    public const int BitRate = 128_000;

    private const string AfConvert = "/usr/bin/afconvert";

    /// <summary>A long song takes a few seconds; anything beyond this is hanging.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<bool> EncodeAsync(string flac, string m4a, CancellationToken cancellationToken)
    {
        string tool;
        string[] arguments;
        if (File.Exists(AfConvert))
        {
            tool = AfConvert;
            arguments = ["-f", "m4af", "-d", "aac", "-b", BitRate.ToString(), flac, m4a];
        }
        else if (FindFfmpeg() is { } ffmpeg)
        {
            tool = ffmpeg;
            // faststart puts the index first, so a player can start before the whole file is there.
            arguments = ["-nostdin", "-loglevel", "error", "-y", "-i", flac, "-vn", "-c:a", "aac", "-b:a", BitRate.ToString(), "-movflags", "+faststart", "-f", "mp4", m4a];
        }
        else
        {
            return false;
        }

        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var message = $"{Path.GetFileName(tool)} exited with {process.ExitCode}: {((await error).Trim() is { Length: > 0 } e ? e : (await output).Trim())}";
                logger.LogWarning("{Message}", message);
                throw new InvalidOperationException(message);
            }
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return true;
    }

    /// <summary>A LaunchAgent's PATH lacks Homebrew, so its folders are tried as well.</summary>
    private static string? FindFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return path.Concat(["/opt/homebrew/bin", "/usr/local/bin"])
            .Where(directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory, "ffmpeg"))
            .FirstOrDefault(File.Exists);
    }
}
