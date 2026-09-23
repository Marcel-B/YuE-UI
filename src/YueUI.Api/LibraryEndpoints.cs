using System.IO.Compression;
using System.Text.RegularExpressions;
using YueUI.Api.Library;

namespace YueUI.Api;

/// <summary>The finished songs on disk: the list, each song's audio and score, and both zipped per song or per run.</summary>
public static partial class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/library", (SongLibrary library) => library.ListRuns());
        // Range requests let the browser's player seek and start before a five-minute FLAC is loaded.
        api.MapGet("/songs/{run}/{song}/audio", (string run, string song, bool? download, SongLibrary library) =>
            SongFile(library, run, song, "audio.flac", "audio/flac", "flac", download == true));
        api.MapGet("/songs/{run}/{song}/score", (string run, string song, SongLibrary library) =>
            SongFile(library, run, song, "score.abc", "text/vnd.abc; charset=utf-8", "abc", download: true));
        api.MapGet("/songs/{run}/{song}/zip", (string run, string song, SongLibrary library) =>
            library.SongDirectory(run, song) is { } directory
                ? Zip($"{FileName(library.TitleOf(run, directory), run)}-{song}.zip", SongEntries(library, run, directory))
                : Results.NotFound());
        api.MapGet("/runs/{run}/zip", (string run, SongLibrary library) =>
            library.SongDirectories(run) is { Count: > 0 } directories
                ? Zip($"{FileName(library.TitleOf(run, directories[0]), run)}.zip", directories.SelectMany(d => SongEntries(library, run, d)))
                : Results.NotFound());
        return api;
    }

    /// <summary>What a song is worth keeping: its audio and its score, named like the single downloads.</summary>
    private static IEnumerable<(string Path, string Name)> SongEntries(SongLibrary library, string run, string directory)
    {
        var name = $"{FileName(library.TitleOf(run, directory), run)}-{Path.GetFileName(directory)}";
        yield return (Path.Combine(directory, "audio.flac"), $"{name}.flac");
        yield return (Path.Combine(directory, "score.abc"), $"{name}.abc");
    }

    /// <summary>
    /// Builds the archive in a temporary file that deletes itself once the response is sent: a run of four full
    /// songs is well over 100 MB, too much to hold in memory, and ZipArchive writes synchronously, which Kestrel's
    /// response stream refuses. FLAC is compressed already, so it is only stored.
    /// </summary>
    internal static IResult Zip(string fileName, IEnumerable<(string Path, string Name)> entries)
    {
        var existing = entries.Where(e => File.Exists(e.Path)).ToList();
        if (existing.Count == 0)
        {
            return Results.NotFound();
        }

        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        try
        {
            using (var archive = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, name) in existing)
                {
                    archive.CreateEntryFromFile(path, name, name.EndsWith(".flac", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                }
            }
            temp.Position = 0;
            return Results.File(temp, "application/zip", fileName);
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    private static IResult SongFile(SongLibrary library, string run, string song, string name, string contentType, string extension, bool download)
    {
        if (library.SongDirectory(run, song) is not { } directory || !File.Exists(Path.Combine(directory, name)))
        {
            return Results.NotFound();
        }
        var fileName = download ? $"{FileName(library.TitleOf(run, directory), run)}-{song}.{extension}" : null;
        return Results.File(Path.Combine(directory, name), contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>The title without characters a file system or a Content-Disposition header dislikes.</summary>
    private static string FileName(string title, string run)
    {
        var name = Unsafe().Replace(title, "").Trim();
        return name.Length > 0 ? name : run;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\p{C}]")]
    private static partial Regex Unsafe();
}
