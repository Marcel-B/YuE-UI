namespace YueUI.Api.Data;

/// <summary>
/// Ratings of songs, one to five stars, by song id (<c>run/songN</c>): songs of one run differ (another seed), so the
/// rating belongs to the song, not to the run. Not rated is no row rather than a zero.
/// </summary>
/// <remarks>Like the titles, ratings of runs deleted from outside (in YuE Studio) stay behind unseen.</remarks>
public sealed class SqliteSongRatingStore(SqliteDatabase database)
{
    public const int MaxRating = 5;

    public IReadOnlyDictionary<string, int> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT song_id, rating FROM song_ratings";
        using var reader = command.ExecuteReader();
        var ratings = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ratings[reader.GetString(0)] = reader.GetInt32(1);
        }
        return ratings;
    }

    /// <param name="rating">1 to <see cref="MaxRating"/>; the table refuses anything else.</param>
    public void Set(string songId, int rating)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO song_ratings (song_id, rating) VALUES ($song, $rating) ON CONFLICT (song_id) DO UPDATE SET rating = excluded.rating",
            ("$song", songId),
            ("$rating", rating));
    }

    public void Remove(string songId)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM song_ratings WHERE song_id = $song", ("$song", songId));
    }

    /// <summary>Every song of the run; a run name never contains a slash, so the prefix cannot reach another run.</summary>
    public void RemoveRun(string run)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "DELETE FROM song_ratings WHERE substr(song_id, 1, length($prefix)) = $prefix",
            ("$prefix", $"{run}/"));
    }
}
