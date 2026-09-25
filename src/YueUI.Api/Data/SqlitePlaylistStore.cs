namespace YueUI.Api.Data;

/// <summary>
/// The playlist's songs (<c>run/songN</c>) in the order they play. On the server rather than in the browser, because
/// the phone's home-screen app, its Safari and the Mac each have their own storage and should still share one playlist.
/// </summary>
/// <remarks>Songs deleted since stay in the table; readers skip them (<see cref="PlaylistEndpoints"/>).</remarks>
public sealed class SqlitePlaylistStore(SqliteDatabase database)
{
    public IReadOnlyList<string> SongIds(long playlistId = SqliteDatabase.DefaultPlaylistId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT song_id FROM playlist_songs WHERE playlist_id = $playlist ORDER BY position";
        command.Parameters.AddWithValue("$playlist", playlistId);
        using var reader = command.ExecuteReader();
        var songIds = new List<string>();
        while (reader.Read())
        {
            songIds.Add(reader.GetString(0));
        }
        return songIds;
    }

    /// <summary>Replaces the playlist's songs in one transaction; the caller passes each song once.</summary>
    public void Replace(IReadOnlyList<string> songIds, long playlistId = SqliteDatabase.DefaultPlaylistId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        SqliteDatabase.Execute(connection, transaction, "DELETE FROM playlist_songs WHERE playlist_id = $playlist", ("$playlist", playlistId));
        for (var position = 0; position < songIds.Count; position++)
        {
            SqliteDatabase.Execute(
                connection,
                transaction,
                "INSERT INTO playlist_songs (playlist_id, position, song_id) VALUES ($playlist, $position, $song)",
                ("$playlist", playlistId),
                ("$position", position),
                ("$song", songIds[position]));
        }
        transaction.Commit();
    }
}
