using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YueUI.Api.Worker;

namespace YueUI.Api.Library;

/// <param name="Id">The folder's name, e.g. <c>My-Song-20260923-201500</c>.</param>
/// <param name="SourceName">The recording's file name.</param>
/// <param name="Task">"melody-full" or "melody-vocal".</param>
/// <param name="Warnings">What SheetSage2 noticed about the recording, e.g. an uncertain key.</param>
/// <param name="Bytes">What the folder takes on disk.</param>
public sealed record TranscriptionInfo(
    string Id,
    string SourceName,
    string? Task,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<string> Warnings,
    long Bytes);

/// <param name="Installed">SheetSage2's environment exists; without it the worker refuses to transcribe.</param>
public sealed record TranscriptionList(bool Installed, IReadOnlyList<TranscriptionInfo> Items);

/// <summary>
/// Reads the transcriptions the worker (this server's or YuE Studio's) wrote to <see cref="YuePaths.TranscriptionsDir"/>:
/// one folder each with <c>score.abc</c>, <c>input.json</c> (source, task), <c>transcription_manifest.json</c>
/// (warnings) and SheetSage2's MIDI and annotation files. Failed attempts (no score) are left out.
/// </summary>
/// <remarks>Folders are looked up among the directory's actual entries, which keeps request paths inside it.</remarks>
public sealed partial class TranscriptionLibrary(YuePaths paths)
{
    public TranscriptionList List() => new(
        paths.SheetSageInstalled,
        [.. Folders().Where(d => File.Exists(Path.Combine(d.FullName, "score.abc"))).Select(Read).OrderByDescending(t => t.CreatedAt)]);

    /// <summary>The transcription's folder, or null if there is none of that name.</summary>
    public string? Directory(string id) =>
        Folders().FirstOrDefault(d => string.Equals(d.Name, id, StringComparison.Ordinal))?.FullName;

    public TranscriptionInfo? Find(string id) => Directory(id) is { } directory ? Read(new DirectoryInfo(directory)) : null;

    /// <summary>Deletes a finished transcription's folder.</summary>
    /// <returns>
    /// False if there is none of that name, or it has no score: the worker may still be writing it, and failed
    /// attempts are not listed, so nobody could have asked for them.
    /// </returns>
    public bool Delete(string id)
    {
        if (Directory(id) is not { } directory || !File.Exists(Path.Combine(directory, "score.abc")))
        {
            return false;
        }
        System.IO.Directory.Delete(directory, recursive: true);
        return true;
    }

    private IEnumerable<DirectoryInfo> Folders()
    {
        var root = new DirectoryInfo(paths.TranscriptionsDir);
        return root.Exists ? root.EnumerateDirectories() : [];
    }

    private static TranscriptionInfo Read(DirectoryInfo directory)
    {
        var input = ReadJson(Path.Combine(directory.FullName, "input.json"));
        var manifest = ReadJson(Path.Combine(directory.FullName, "transcription_manifest.json"));
        var stamp = Stamp().Match(directory.Name);
        DateTimeOffset? createdAt = stamp.Success
            && DateTime.TryParseExact(stamp.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local)
            : directory.CreationTimeUtc;
        return new TranscriptionInfo(
            directory.Name,
            Text(input?["source_name"]) ?? (stamp.Success ? directory.Name[..stamp.Index] : directory.Name),
            Text(input?["task"]),
            createdAt,
            [.. (manifest?["warnings"] as JsonArray ?? []).Select(w => w is JsonValue v && v.TryGetValue<string>(out var s) ? s : w?.ToJsonString() ?? "")],
            SongLibrary.Size(directory));
    }

    private static JsonObject? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // The worker names the folder "{audio stem}-{yyyyMMdd-HHmmss}".
    [GeneratedRegex(@"-(\d{8}-\d{6})$")]
    private static partial Regex Stamp();
}
