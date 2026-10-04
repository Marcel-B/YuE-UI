using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Images;

/// <param name="Output">Where the picture is written, as JPEG.</param>
public sealed record ImageJob(ImageModel Model, string Prompt, long Seed, int Width, int Height, string Output);

/// <summary>The script's measurements; null where it could not tell.</summary>
public sealed record ImageResult(double? LoadSeconds, double? PaintSeconds, double? PeakMemoryGb);

public sealed class ImageException(string message, int status = StatusCodes.Status500InternalServerError) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Paints pictures. Tests replace it; the real one runs mflux.</summary>
public interface IImageEngine
{
    /// <summary>The Python environment with mflux is there.</summary>
    bool Installed { get; }

    /// <summary>Whether the model's repository is already in the cache.</summary>
    bool Downloaded(ImageModel model);

    /// <summary>Whether a Hugging Face token is there for gated models.</summary>
    bool TokenFound { get; }

    /// <param name="progress">Hears the stage (loading, painting) and the share of the steps done.</param>
    Task<ImageResult> PaintAsync(ImageJob job, Action<string, double> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>Images/yueui_image.py</c> with mflux's Python, one process per picture, like the speech lab: the model is
/// loaded for the picture and its memory freed with the process, since on 24 GB it must not stay beside YuE2.
/// </summary>
public sealed class MfluxEngine(IOptions<ImageOptions> options, ILogger<MfluxEngine> logger) : IImageEngine
{
    /// <summary>What the script's own events start with; everything else is mflux talking.</summary>
    private const string EventPrefix = "YUEUI ";

    private const int TailLines = 30;

    public static string Script => Path.Combine(AppContext.BaseDirectory, "Images", "yueui_image.py");

    public bool Installed => File.Exists(options.Value.ResolvedPython);

    public bool TokenFound => Token() is not null;

    public bool Downloaded(ImageModel model) =>
        Directory.Exists(Path.Combine(options.Value.ResolvedModelCache, "hub", $"models--{model.Repo.Replace("/", "--", StringComparison.Ordinal)}", "snapshots"));

    public async Task<ImageResult> PaintAsync(ImageJob job, Action<string, double> progress, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!Installed)
        {
            throw new ImageException($"mflux is not installed ({settings.ResolvedPython}); run deploy/install-images.sh.", StatusCodes.Status501NotImplemented);
        }
        var start = new ProcessStartInfo(settings.ResolvedPython)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Script);
        start.Environment["HF_HOME"] = settings.ResolvedModelCache;
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["TQDM_DISABLE"] = "1";
        if (Token() is { } token)
        {
            start.Environment["HF_TOKEN"] = token;
        }

        var request = new JsonObject
        {
            ["model"] = job.Model.Model,
            ["quantize"] = job.Model.Quantize,
            ["steps"] = job.Model.Steps,
            ["prompt"] = job.Prompt,
            ["seed"] = job.Seed,
            ["width"] = job.Width,
            ["height"] = job.Height,
            ["output"] = job.Output,
        };

        using var process = Process.Start(start) ?? throw new ImageException($"Could not start {settings.ResolvedPython}.");
        logger.LogInformation("Painting with {Model} (pid {Pid})", job.Model.Model, process.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        var tail = new Queue<string>();
        ImageResult? result = null;
        try
        {
            await process.StandardInput.WriteAsync(request.ToJsonString());
            process.StandardInput.Close();
            var errors = PumpAsync(process.StandardError, line => Keep(tail, line), timeout.Token);
            await PumpAsync(process.StandardOutput, line =>
            {
                if (!line.StartsWith(EventPrefix, StringComparison.Ordinal))
                {
                    Keep(tail, line);
                    return;
                }
                var data = JsonNode.Parse(line[EventPrefix.Length..]);
                switch ((string?)data?["event"])
                {
                    case "stage":
                        progress((string?)data["stage"] ?? "painting", (double?)data["fraction"] ?? 0);
                        break;
                    case "done":
                        result = new ImageResult((double?)data["loadSeconds"], (double?)data["paintSeconds"], (double?)data["peakMemoryGb"]);
                        break;
                }
            }, timeout.Token);
            await errors;
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new ImageException($"The model took longer than {settings.Timeout.TotalMinutes:0} minutes.");
            }
            throw;
        }
        if (process.ExitCode != 0 || result is null || !File.Exists(job.Output))
        {
            lock (tail)
            {
                logger.LogWarning("Painting with {Model} failed:\n{Output}", job.Model.Model, string.Join('\n', tail));
                throw new ImageException(FailureMessage(tail));
            }
        }
        return result;
    }

    /// <summary>
    /// The script prints its own "Error: …" line where it knows better (a gated repository, a missing package); else the
    /// traceback's last line says what broke.
    /// </summary>
    public static string FailureMessage(IEnumerable<string> output)
    {
        var lines = output.Where(l => l.Trim().Length > 0).ToList();
        var line = lines.LastOrDefault(l => l.StartsWith("Error", StringComparison.Ordinal)) ?? lines.LastOrDefault();
        return line is null ? "The model painted no picture." : line.Trim();
    }

    /// <summary>Read for every picture, so a token put there later works without a restart.</summary>
    private string? Token()
    {
        try
        {
            var path = options.Value.ResolvedTokenFile;
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } token ? token : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Keep(Queue<string> tail, string line)
    {
        lock (tail)
        {
            tail.Enqueue(line);
            while (tail.Count > TailLines)
            {
                tail.Dequeue();
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> line, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } read)
        {
            line(read);
        }
    }
}
