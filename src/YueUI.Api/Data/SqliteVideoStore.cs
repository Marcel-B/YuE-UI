using System.Globalization;
using Microsoft.Data.Sqlite;
using YueUI.Api.Video;

namespace YueUI.Api.Data;

/// <summary>
/// Music videos made of songs (<see cref="VideoState"/>). Each has a folder in <c>videos/&lt;id&gt;/</c> next to the
/// database with the layers the browser drew and, once done, the video (<c>video.mp4</c>).
/// </summary>
public sealed class SqliteVideoStore(SqliteDatabase database)
{
    public const string VideoFile = "video.mp4";

    public string Folder(string id) => Path.Combine(database.Directory, "videos", id);

    public string VideoPath(string id) => Path.Combine(Folder(id), VideoFile);

    public VideoLayers Layers(VideoState video) => new(
        Path.Combine(Folder(video.Id), "background.png"),
        video.ShowCover ? Path.Combine(Folder(video.Id), "cover.png") : null,
        video.ShowTitle ? Path.Combine(Folder(video.Id), "title.png") : null,
        video.Motion switch
        {
            VideoMotions.Particles => [Path.Combine(Folder(video.Id), "particles.png")],
            VideoMotions.Plasma => [.. Enumerable.Range(0, VideoLayout.PlasmaFrames).Select(i => Path.Combine(Folder(video.Id), $"plasma{i}.png"))],
            _ => [],
        });

    /// <summary>The song's videos, newest first, as the dialog lists them.</summary>
    public IReadOnlyList<VideoState> ForSong(string songId) =>
        Query("SELECT * FROM song_videos WHERE song_id = $song ORDER BY created_at DESC", ("$song", songId));

    public IReadOnlyList<VideoState> Unfinished() =>
        Query("SELECT * FROM song_videos WHERE stage NOT IN ('done', 'failed', 'cancelled') ORDER BY created_at");

    public VideoState? Get(string id) => Query("SELECT * FROM song_videos WHERE id = $id", ("$id", id)).FirstOrDefault();

    public void Add(VideoState video)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO song_videos (id, song_id, title, format, effect, color, motion, show_cover, show_title, stage, message, bytes, created_at, updated_at)
            VALUES ($id, $song, $title, $format, $effect, $color, $motion, $showCover, $showTitle, $stage, $message, $bytes, $created, $updated)
            """,
            ("$id", video.Id),
            ("$song", video.SongId),
            ("$title", video.Title),
            ("$format", video.Format),
            ("$effect", video.Effect),
            ("$color", (object?)video.Color ?? DBNull.Value),
            ("$motion", video.Motion),
            ("$showCover", video.ShowCover ? 1 : 0),
            ("$showTitle", video.ShowTitle ? 1 : 0),
            ("$stage", video.Stage),
            ("$message", (object?)video.Message ?? DBNull.Value),
            ("$bytes", (object?)video.Bytes ?? DBNull.Value),
            ("$created", Time(video.CreatedAt)),
            ("$updated", Time(video.UpdatedAt)));
    }

    /// <returns>False when the video was deleted meanwhile; it is not brought back.</returns>
    public bool Update(VideoState video)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(
            connection,
            null,
            "UPDATE song_videos SET stage = $stage, message = $message, bytes = $bytes, updated_at = $updated WHERE id = $id",
            ("$id", video.Id),
            ("$stage", video.Stage),
            ("$message", (object?)video.Message ?? DBNull.Value),
            ("$bytes", (object?)video.Bytes ?? DBNull.Value),
            ("$updated", Time(video.UpdatedAt))) > 0;
    }

    /// <summary>Removes the video with its layers.</summary>
    public void Remove(string id)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, "DELETE FROM song_videos WHERE id = $id", ("$id", id));
        }
        DeleteFolder(id);
    }

    /// <summary>Every video of the song, when the song is deleted.</summary>
    public void RemoveSong(string songId) => RemoveWhere("song_id = $key", songId);

    /// <summary>Every video of the run's songs; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run) => RemoveWhere("substr(song_id, 1, length($key)) = $key", $"{run}/");

    private void RemoveWhere(string condition, string key)
    {
        var ids = Query($"SELECT * FROM song_videos WHERE {condition}", ("$key", key)).Select(v => v.Id).ToList();
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(connection, null, $"DELETE FROM song_videos WHERE {condition}", ("$key", key));
        }
        foreach (var id in ids)
        {
            DeleteFolder(id);
        }
    }

    private void DeleteFolder(string id)
    {
        try
        {
            if (Directory.Exists(Folder(id)))
            {
                Directory.Delete(Folder(id), recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private List<VideoState> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var videos = new List<VideoState>();
        while (reader.Read())
        {
            videos.Add(Read(reader));
        }
        return videos;
    }

    private static VideoState Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        SongId = reader.GetString(reader.GetOrdinal("song_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Format = reader.GetString(reader.GetOrdinal("format")),
        Effect = reader.GetString(reader.GetOrdinal("effect")),
        Color = reader.IsDBNull(reader.GetOrdinal("color")) ? null : reader.GetString(reader.GetOrdinal("color")),
        Motion = reader.GetString(reader.GetOrdinal("motion")),
        ShowCover = reader.GetInt64(reader.GetOrdinal("show_cover")) != 0,
        ShowTitle = reader.GetInt64(reader.GetOrdinal("show_title")) != 0,
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Message = reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString(reader.GetOrdinal("message")),
        Bytes = reader.IsDBNull(reader.GetOrdinal("bytes")) ? null : reader.GetInt64(reader.GetOrdinal("bytes")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")), CultureInfo.InvariantCulture),
    };

    private static string Time(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);
}
