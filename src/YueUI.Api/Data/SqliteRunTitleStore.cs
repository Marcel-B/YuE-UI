namespace YueUI.Api.Data;

/// <summary>
/// Titles given to runs after the fact. The worker's title stays in the song files and names the run folder, which
/// is also the song id that playlists, links and the worker's song states hold on to; renaming the folder would break
/// all of them (and YuE Studio's view of the song), so the new title is kept here and laid over the worker's.
/// </summary>
/// <remarks>A run deleted from outside (in YuE Studio) leaves its row behind; it costs nothing and is never shown.</remarks>
public sealed class SqliteRunTitleStore(SqliteDatabase database)
{
    public IReadOnlyDictionary<string, string> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, title FROM run_titles";
        using var reader = command.ExecuteReader();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            titles[reader.GetString(0)] = reader.GetString(1);
        }
        return titles;
    }

    public string? Get(string run)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM run_titles WHERE run_id = $run";
        command.Parameters.AddWithValue("$run", run);
        return command.ExecuteScalar() as string;
    }

    public void Set(string run, string title)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT INTO run_titles (run_id, title) VALUES ($run, $title) ON CONFLICT (run_id) DO UPDATE SET title = excluded.title",
            ("$run", run),
            ("$title", title));
    }

    public void Remove(string run)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM run_titles WHERE run_id = $run", ("$run", run));
    }
}
