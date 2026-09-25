using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace YueUI.Api.Data;

/// <summary>
/// The SQLite file this app keeps its own state in (the songs stay in YuE Studio's folders). The file and its tables
/// are created on first use, so a server whose data folder is not writable still starts; only what needs the file fails.
/// </summary>
/// <remarks>
/// Every call opens its own connection; Microsoft.Data.Sqlite pools them, and SQLite's file lock serializes the
/// writers. The schema carries its version in <c>PRAGMA user_version</c>: a later change is a new step in
/// <see cref="EnsureCreated"/>, applied in order to a file an earlier release wrote; never edit an old step.
/// Version 1: playlists and their songs. There is one playlist (<see cref="DefaultPlaylistId"/>) so far, the table
/// is there so that more can follow without moving the songs.
/// </remarks>
public sealed class SqliteDatabase(IOptions<DataOptions> options)
{
    public const long DefaultPlaylistId = 1;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.Value.ResolvedPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    private readonly Lock _gate = new();
    private bool _ready;

    public string Path { get; } = options.Value.ResolvedPath;

    /// <summary>An open connection with foreign keys enforced, on a file whose schema is current.</summary>
    public SqliteConnection Open()
    {
        EnsureCreated();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON");
        return connection;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command.ExecuteNonQuery();
    }

    private void EnsureCreated()
    {
        if (_ready)
        {
            return;
        }
        lock (_gate)
        {
            if (_ready)
            {
                return;
            }
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var current = (long)version.ExecuteScalar()!;
            if (current < 1)
            {
                Execute(
                    connection,
                    null,
                    """
                    CREATE TABLE playlists (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name TEXT NOT NULL,
                        created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                    );
                    CREATE TABLE playlist_songs (
                        playlist_id INTEGER NOT NULL REFERENCES playlists (id) ON DELETE CASCADE,
                        position INTEGER NOT NULL,
                        song_id TEXT NOT NULL,
                        PRIMARY KEY (playlist_id, position),
                        UNIQUE (playlist_id, song_id)
                    );
                    INSERT INTO playlists (id, name) VALUES (1, 'Playlist');
                    PRAGMA user_version = 1;
                    """);
            }
            _ready = true;
        }
    }
}
