using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YueUI.Api.Worker;

namespace YueUI.Api.Library;

/// <param name="Id">The run folder's name, e.g. <c>20260921-165850-Neon-Night-Struggle</c>.</param>
/// <param name="Style">The style prompt, from the first song's <c>request.json</c>; all songs of a run share it.</param>
/// <param name="Bytes">What the whole run folder takes on disk.</param>
public sealed record RunInfo(
    string Id,
    string Title,
    DateTimeOffset? CreatedAt,
    string Style,
    string Lyrics,
    IReadOnlyList<SongInfo> Songs,
    long Bytes);

/// <param name="Id"><c>run/songN</c>, the key for the song endpoints and the worker's song states.</param>
/// <param name="Quality">"draft" or "full" once synthesized.</param>
/// <param name="CanRender">Its tokens are saved, so it can be synthesized again (a draft at full quality).</param>
/// <param name="Bytes">What the song folder takes on disk: audio, tokens and the worker's intermediate files.</param>
public sealed record SongInfo(
    string Id,
    int Index,
    long? Seed,
    string? Quality,
    double? Seconds,
    bool HasAudio,
    bool HasScore,
    bool CanRender,
    long Bytes);

/// <summary>
/// Reads the songs the worker (this server's or YuE Studio's) wrote to <see cref="YuePaths.OutputDir"/>:
/// one folder per run, one <c>songN</c> folder per song with <c>audio.flac</c>, <c>score.abc</c>,
/// <c>request.json</c> (the prompt) and <c>result.json</c> (title, quality, length).
/// </summary>
/// <remarks>Only names matching the worker's own patterns are accepted, which also keeps request paths inside the library.</remarks>
public sealed partial class SongLibrary(YuePaths paths)
{
    public string OutputDir => paths.OutputDir;

    public IReadOnlyList<RunInfo> ListRuns()
    {
        var root = new DirectoryInfo(paths.OutputDir);
        if (!root.Exists)
        {
            return [];
        }
        return
        [
            .. root.EnumerateDirectories()
                .Where(d => RunName().IsMatch(d.Name))
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Select(ReadRun)
                // A run still tokenizing has no song folders yet; the queue shows it.
                .Where(r => r.Songs.Count > 0),
        ];
    }

    /// <summary>The song's folder, or null for a name the worker would not write or a song that does not exist.</summary>
    public string? SongDirectory(string run, string song)
    {
        if (!RunName().IsMatch(run) || !SongName().IsMatch(song))
        {
            return null;
        }
        var directory = Path.Combine(paths.OutputDir, run, song);
        return Directory.Exists(directory) ? directory : null;
    }

    /// <summary>The run's song folders in order, or null for a name the worker would not write or a run that does not exist.</summary>
    public IReadOnlyList<string>? SongDirectories(string run)
    {
        var directory = new DirectoryInfo(Path.Combine(paths.OutputDir, run));
        if (!RunName().IsMatch(run) || !directory.Exists)
        {
            return null;
        }
        return [.. SongFolders(directory).Select(d => d.FullName)];
    }

    /// <summary>Deletes the song's folder, and the run's folder with it once no song is left.</summary>
    /// <returns>False for a name the worker would not write or a song that does not exist.</returns>
    public bool DeleteSong(string run, string song)
    {
        if (SongDirectory(run, song) is not { } directory)
        {
            return false;
        }
        Directory.Delete(directory, recursive: true);
        var runDirectory = new DirectoryInfo(Path.Combine(paths.OutputDir, run));
        if (!SongFolders(runDirectory).Any())
        {
            runDirectory.Delete(recursive: true);
        }
        return true;
    }

    /// <summary>Deletes the run's folder with all its songs.</summary>
    /// <returns>False for a name the worker would not write or a run that does not exist.</returns>
    public bool DeleteRun(string run)
    {
        if (SongDirectories(run) is null)
        {
            return false;
        }
        Directory.Delete(Path.Combine(paths.OutputDir, run), recursive: true);
        return true;
    }

    /// <summary>
    /// Maps a path the worker reports (<c>…/run/songN/audio.flac</c> or the song folder) to <c>run/songN</c>.
    /// </summary>
    public string IdFor(string path)
    {
        var directory = Path.GetFileName(path) == "audio.flac" ? Path.GetDirectoryName(path) ?? "" : path;
        directory = directory.TrimEnd(Path.DirectorySeparatorChar);
        return $"{Path.GetFileName(Path.GetDirectoryName(directory))}/{Path.GetFileName(directory)}";
    }

    /// <summary>The run's title for file names: from its songs, else from the folder name's slug.</summary>
    public string TitleOf(string run, string songDirectory) =>
        ReadJson(Path.Combine(songDirectory, "result.json")) is { } result && Text(result["title"]) is { Length: > 0 } title
            ? title
            : TitleFromName(run);

    /// <summary>The length of the song's audio as the worker measured it, or null before it is synthesized.</summary>
    public double? SecondsOf(string songDirectory) =>
        ReadJson(Path.Combine(songDirectory, "result.json")) is { } result ? Number(result["audio_seconds"]) : null;

    private RunInfo ReadRun(DirectoryInfo run)
    {
        var songs = new List<SongInfo>();
        string title = "", style = "", lyrics = "";
        foreach (var folder in SongFolders(run))
        {
            // result.json once synthesized, tokens.json while only the tokens exist; both carry title and quality.
            var result = ReadJson(Path.Combine(folder.FullName, "result.json")) ?? ReadJson(Path.Combine(folder.FullName, "tokens.json"));
            var request = ReadJson(Path.Combine(folder.FullName, "request.json"));
            if (title.Length == 0 && result is not null)
            {
                title = Text(result["title"]) ?? "";
            }
            if (style.Length == 0 && request is not null)
            {
                style = Text(request["style"]) ?? "";
                lyrics = Text(request["lyrics"]) ?? "";
            }

            var audio = File.Exists(Path.Combine(folder.FullName, "audio.flac"));
            songs.Add(new SongInfo(
                $"{run.Name}/{folder.Name}",
                int.Parse(folder.Name.AsSpan(4), CultureInfo.InvariantCulture),
                request is null ? null : (long?)Number(request["seed"]),
                audio && result is not null ? Text(result["quality"]) : null,
                audio && result is not null ? Number(result["audio_seconds"]) : null,
                audio,
                File.Exists(Path.Combine(folder.FullName, "score.abc")),
                File.Exists(Path.Combine(folder.FullName, "semantic.npy")),
                Size(folder)));
        }

        return new RunInfo(run.Name, title.Length > 0 ? title : TitleFromName(run.Name), CreatedAt(run.Name), style, lyrics, songs, Size(run));
    }

    /// <summary>The folder's files, all levels down; a file the worker deletes meanwhile counts as nothing.</summary>
    internal static long Size(DirectoryInfo directory)
    {
        long bytes = 0;
        foreach (var file in directory.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try
            {
                bytes += file.Length;
            }
            catch (IOException)
            {
            }
        }
        return bytes;
    }

    private static IEnumerable<DirectoryInfo> SongFolders(DirectoryInfo run) =>
        run.EnumerateDirectories()
            .Where(d => SongName().IsMatch(d.Name))
            .OrderBy(d => int.Parse(d.Name.AsSpan(4), CultureInfo.InvariantCulture));

    /// <summary>"20260921-165850-Neon-Night-Struggle" → "Neon Night Struggle" (the slug has lost umlauts and punctuation).</summary>
    private static string TitleFromName(string run) =>
        run.Length > 16 ? run[16..].Replace('-', ' ').Trim() : "";

    /// <summary>The worker names runs by its local time.</summary>
    private static DateTimeOffset? CreatedAt(string run) =>
        DateTime.TryParseExact(run.AsSpan(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created)
            ? new DateTimeOffset(created)
            : null;

    /// <summary>Null when missing or half written (the worker may be writing it right now).</summary>
    private static JsonObject? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary>What <c>yue2_worker.py</c> names run folders: a timestamp, the title's slug, and "b"s against collisions.</summary>
    [GeneratedRegex(@"^\d{8}-\d{6}(-[A-Za-z0-9-]+)?b*$")]
    private static partial Regex RunName();

    [GeneratedRegex(@"^song\d{1,4}$")]
    private static partial Regex SongName();
}
