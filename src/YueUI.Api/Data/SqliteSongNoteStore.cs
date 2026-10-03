namespace YueUI.Api.Data;

/// <summary>
/// Free notes written to songs, by song id (<c>run/songN</c>), like the ratings: songs of one run differ, so a note
/// about how one sounds belongs to that song. No note is no row rather than an empty one.
/// </summary>
/// <remarks>Like the ratings, notes of runs deleted from outside (in YuE Studio) stay behind unseen.</remarks>
public sealed class SqliteSongNoteStore(SqliteDatabase database, TimeProvider time)
{
    public IReadOnlyDictionary<string, string> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT song_id, note FROM song_notes";
        using var reader = command.ExecuteReader();
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            notes[reader.GetString(0)] = reader.GetString(1);
        }
        return notes;
    }

    public void Set(string songId, string note)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO song_notes (song_id, note, updated_at) VALUES ($song, $note, $at) ON CONFLICT (song_id) DO UPDATE SET note = excluded.note, updated_at = excluded.updated_at",
            ("$song", songId),
            ("$note", note),
            ("$at", time.GetUtcNow().ToString("O")));
    }

    public void Remove(string songId)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM song_notes WHERE song_id = $song", ("$song", songId));
    }

    /// <summary>Every song of the run; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "DELETE FROM song_notes WHERE substr(song_id, 1, length($prefix)) = $prefix",
            ("$prefix", $"{run}/"));
    }
}
