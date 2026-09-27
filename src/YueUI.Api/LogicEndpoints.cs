using System.Text.Json;
using Microsoft.Extensions.Options;
using YueToLogic.Core.Conversion;
using YueToLogic.Core.Diagnostics;
using YueToLogic.Core.Logic;
using YueToLogic.Core.Serialization;
using YueUI.Api.Library;
using YueUI.Api.Logic;

namespace YueUI.Api;

/// <summary>A MIDI file read back into a score.</summary>
/// <param name="Abc">The <c>score.abc</c> for the next song.</param>
/// <param name="Warnings">What could not be carried over, e.g. a track that was ignored.</param>
public sealed record MidiScore(string Abc, IReadOnlyList<string> Warnings);

/// <summary>
/// A song as a Logic Pro project, and a MIDI file edited in Logic back into a score, both built in this process by
/// <c>YueToLogic.Core</c> (formerly the separate yue-to-logic-pro service).
/// </summary>
public static class LogicEndpoints
{
    /// <summary>The warnings of an export (compact JSON array of <see cref="Diagnostic"/>), since the body is the ZIP.</summary>
    public const string DiagnosticsHeader = "X-YueToLogic-Diagnostics";

    /// <summary>A song's MIDI file is a few hundred kilobytes, even with every generated track in it.</summary>
    public const long MaxMidiBytes = 4 * 1024 * 1024;

    private static readonly YueToLogicJsonContext CompactJson = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    });

    public static RouteGroupBuilder MapLogicEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/songs/{run}/{song}/logic", ExportAsync);
        // The way back: a song edited in Logic and exported as MIDI becomes the score of a new song.
        api.MapPost("/midi/abc", MidiToAbcAsync).DisableAntiforgery();
        return api;
    }

    private static async Task<IResult> ExportAsync(
        string run,
        string song,
        SongLibrary library,
        IScoreConverter converter,
        ILogicProjectWriter writer,
        IOptions<LogicOptions> options,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (library.SongDirectory(run, song) is not { } directory
            || !File.Exists(Path.Combine(directory, "audio.flac"))
            || !File.Exists(Path.Combine(directory, "score.abc")))
        {
            return Results.NotFound();
        }

        const string title = "This song cannot become a Logic project.";
        var name = $"{LibraryEndpoints.FileName(library.TitleOf(run, directory), run)}-{song}";
        var conversionOptions = new ConversionOptions();
        ConversionResult converted;
        await using (var score = File.OpenRead(Path.Combine(directory, "score.abc")))
        {
            converted = await converter.ConvertAsync(score, conversionOptions, cancellationToken);
        }
        if (!converted.Success)
        {
            return Refusal(title, converted.Diagnostics);
        }

        // ZipArchive writes synchronously, which Kestrel does not allow on the response; the ZIP is as large as the
        // FLAC, so it goes into a temporary file (deleted once sent) rather than into memory.
        var zip = new FileStream(
            Path.Combine(Path.GetTempPath(), $"yueui-logic-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        LogicProjectResult logic;
        try
        {
            await using var audio = File.OpenRead(Path.Combine(directory, "audio.flac"));
            using var sink = new ZipLogicPackageSink(zip, name);
            logic = await writer.WriteAsync(
                converted.Score!,
                new LogicAudio(audio),
                sink,
                new LogicProjectOptions
                {
                    ProjectName = name,
                    SplitRegionsAtSections = options.Value.SplitSections,
                    Channels = conversionOptions.MidiChannels,
                },
                cancellationToken);
        }
        catch
        {
            await zip.DisposeAsync();
            throw;
        }

        IReadOnlyList<Diagnostic> diagnostics = [.. converted.Diagnostics, .. logic.Diagnostics];
        if (!logic.Success)
        {
            await zip.DisposeAsync();
            return Refusal(title, diagnostics);
        }

        var warnings = diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToArray();
        if (warnings.Length > 0)
        {
            // JSON escapes non-ASCII characters by default, so the value is a valid header.
            context.Response.Headers[DiagnosticsHeader] = JsonSerializer.Serialize(warnings, CompactJson.DiagnosticArray);
        }
        zip.Position = 0;
        return Results.File(zip, "application/zip", $"{name}.logicx.zip");
    }

    private static async Task<IResult> MidiToAbcAsync(IFormFile? file, IMidiToAbcConverter converter, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Results.Problem(title: "Send the MIDI file as form field 'file'.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (file.Length > MaxMidiBytes)
        {
            return Results.Problem(
                title: $"A MIDI file may have at most {MaxMidiBytes / (1024 * 1024)} MB.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        await using var midi = file.OpenReadStream();
        var result = await converter.ConvertAsync(midi, new MidiToAbcOptions(), cancellationToken);
        if (!result.Success || result.Abc is not { Length: > 0 } abc)
        {
            return Refusal("No score can be read from this MIDI file.", result.Diagnostics);
        }
        return Results.Ok(new MidiScore(abc, Messages(result.Diagnostics, DiagnosticSeverity.Warning)));
    }

    private static List<string> Messages(IEnumerable<Diagnostic> diagnostics, DiagnosticSeverity severity) =>
        diagnostics.Where(d => d.Severity == severity).Select(d => d.Message).ToList();

    /// <summary>The input itself cannot be converted; its errors say why. The browser shows the detail alone.</summary>
    private static IResult Refusal(string title, IEnumerable<Diagnostic> diagnostics) =>
        Results.Problem(
            title: title,
            detail: string.Join(" ", [title, .. Messages(diagnostics, DiagnosticSeverity.Error)]),
            statusCode: StatusCodes.Status422UnprocessableEntity);
}
