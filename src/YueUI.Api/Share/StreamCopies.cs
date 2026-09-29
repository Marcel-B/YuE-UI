using System.Collections.Concurrent;
using YueUI.Api.Data;

namespace YueUI.Api.Share;

/// <summary>
/// The AAC copies the player streams instead of the FLAC. The Mac sits behind a home connection whose upload is a
/// fraction of its download: a FLAC runs at about 1 Mbit/s, which on the road stalled or took long to start, while
/// 192 kbit/s AAC is a sixth of that and sounds the same through a phone. The FLAC stays what downloads, exports and
/// the Logic page use.
/// </summary>
/// <remarks>
/// Copies live in <c>stream/</c> next to the database (a song's as <c>stream/&lt;run&gt;/songN.m4a</c>, a version's
/// beside its FLAC in <c>versions/</c>), never in YuE Studio's song folder. <see cref="StreamCopyMaker"/> makes them
/// when a song or version is finished; older ones are made on their first play. A render writes the song's FLAC
/// anew in place, so a copy older than its FLAC counts as missing. Without an encoder, or when encoding fails, the
/// caller serves the FLAC as before.
/// </remarks>
public sealed class StreamCopies(SqliteDatabase database, SqliteVersionStore versions, IAudioEncoder encoder, ILogger<StreamCopies> logger)
{
    public const int BitRate = 192_000;

    /// <summary>One encoding per copy even when the player's range requests and <see cref="StreamCopyMaker"/> meet.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _making = new();

    private string Root => Path.Combine(database.Directory, "stream");

    /// <summary>Where the song's copy lies; <paramref name="run"/> and <paramref name="song"/> must be checked names.</summary>
    public string SongPath(string run, string song) => Path.Combine(Root, run, $"{song}.m4a");

    /// <summary>The song's copy, made first if needed.</summary>
    /// <returns>Its path, or null when there is none and the FLAC has to do.</returns>
    public Task<string?> SongAsync(string run, string song, string flac) => GetAsync(flac, SongPath(run, song));

    /// <summary>The version's copy, made first if needed.</summary>
    /// <returns>Its path, or null when there is none and the FLAC has to do.</returns>
    public Task<string?> VersionAsync(string id) => GetAsync(versions.FilePath(id), versions.StreamPath(id));

    /// <summary>Deletes the song's copy; the song is gone.</summary>
    public void ForgetSong(string run, string song) => Delete(() => File.Delete(SongPath(run, song)));

    /// <summary>Deletes the copies of the run's songs; the run is gone.</summary>
    public void ForgetRun(string run) => Delete(() =>
    {
        var directory = Path.Combine(Root, run);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    private async Task<string?> GetAsync(string source, string target)
    {
        if (!File.Exists(source))
        {
            return null;
        }
        if (IsFresh(source, target))
        {
            return target;
        }
        var making = _making.GetOrAdd(target, _ => new Lazy<Task<bool>>(() => MakeAsync(source, target)));
        try
        {
            return await making.Value ? target : null;
        }
        finally
        {
            _making.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(target, making));
        }
    }

    private static bool IsFresh(string source, string target) =>
        File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source);

    /// <summary>
    /// Into a temporary name first, so a player never gets half a file. Not cancelled with the request: the player
    /// drops range requests all the time, and the copy is wanted anyway.
    /// </summary>
    private async Task<bool> MakeAsync(string source, string target)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileNameWithoutExtension(target)}-{Guid.NewGuid():N}.m4a");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!await encoder.EncodeAsync(source, temp, AudioFormat.M4a, BitRate, CancellationToken.None))
            {
                return false;
            }
            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not make the streaming copy of {Source}", source);
            return false;
        }
        finally
        {
            Delete(() => File.Delete(temp));
        }
    }

    private static void Delete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
