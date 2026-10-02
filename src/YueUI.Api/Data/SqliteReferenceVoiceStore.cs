using System.Globalization;
using Microsoft.Data.Sqlite;
using YueUI.Api.Voices;

namespace YueUI.Api.Data;

/// <summary>
/// The reference voices songs are sung with, once this server converts them itself (before, ChangeMyVoice kept them).
/// Each is a mono 44.1 kHz WAV of at most 25 seconds in <c>voices/</c> next to the database, named by its id.
/// </summary>
public sealed class SqliteReferenceVoiceStore(SqliteDatabase database)
{
    public string FilePath(string id) => Path.Combine(database.Directory, "voices", $"{id}.wav");

    public IReadOnlyList<ReferenceVoice> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM reference_voices ORDER BY created_at, rowid";
        using var reader = command.ExecuteReader();
        List<ReferenceVoice> voices = [];
        while (reader.Read())
        {
            voices.Add(new ReferenceVoice(
                reader.GetString(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("label")),
                reader.GetDouble(reader.GetOrdinal("seconds")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")), CultureInfo.InvariantCulture)));
        }
        return voices;
    }

    public ReferenceVoice? Get(string id) => All().FirstOrDefault(v => v.Id == id);

    /// <summary>Adds the voice or, with the same id (an import running again), replaces it.</summary>
    public void Add(ReferenceVoice voice)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            "INSERT OR REPLACE INTO reference_voices (id, label, seconds, created_at) VALUES ($id, $label, $seconds, $created)",
            ("$id", voice.Id),
            ("$label", voice.Label),
            ("$seconds", voice.Seconds),
            ("$created", (voice.CreatedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture)));
    }

    /// <returns>False when there was no such voice.</returns>
    public bool Remove(string id)
    {
        int removed;
        using (var connection = database.Open())
        {
            removed = SqliteDatabase.Execute(connection, null, "DELETE FROM reference_voices WHERE id = $id", ("$id", id));
        }
        try
        {
            File.Delete(FilePath(id));
        }
        catch (IOException)
        {
        }
        return removed > 0;
    }
}
