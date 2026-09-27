using System.Text.Json;
using Microsoft.Data.Sqlite;
using YueUI.Api.Logic;

namespace YueUI.Api.Data;

/// <summary>
/// The Logic page's presets, one row per name. Names are matched through <c>name_key</c>, the name in invariant upper
/// case, because SQLite itself folds only ASCII letters.
/// </summary>
public sealed class SqliteLogicPresetStore(SqliteDatabase database)
{
    private const string Columns = "id, name, form, updated_at";

    /// <summary>All presets, ordered by name.</summary>
    public IReadOnlyList<LogicPreset> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM logic_presets ORDER BY name_key, id";
        using var reader = command.ExecuteReader();
        var presets = new List<LogicPreset>();
        while (reader.Read())
        {
            presets.Add(Read(reader));
        }
        return presets;
    }

    /// <summary>Adds the preset, or replaces the one of that name (case-insensitively); the spelling follows the latest save.</summary>
    /// <returns>The preset as stored, and whether it was new.</returns>
    public (LogicPreset Preset, bool Created) Save(string name, JsonElement form)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var created = Save(connection, transaction, name, form);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM logic_presets WHERE name_key = $key";
        command.Parameters.AddWithValue("$key", Key(name));
        LogicPreset preset;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            preset = Read(reader);
        }
        transaction.Commit();
        return (preset, created);
    }

    /// <summary>Saves several presets in one transaction, for the import from yue-to-logic-pro.</summary>
    public int SaveAll(IEnumerable<(string Name, JsonElement Form)> presets)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var (name, form) in presets)
        {
            Save(connection, transaction, name, form);
            count++;
        }
        transaction.Commit();
        return count;
    }

    public bool Delete(string name)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(connection, null, "DELETE FROM logic_presets WHERE name_key = $key", ("$key", Key(name))) > 0;
    }

    private static bool Save(SqliteConnection connection, SqliteTransaction transaction, string name, JsonElement form)
    {
        var key = Key(name);
        var existed = SqliteDatabase.Execute(
            connection,
            transaction,
            """
            UPDATE logic_presets SET name = $name, form = $form, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE name_key = $key
            """,
            ("$key", key),
            ("$name", name),
            ("$form", form.GetRawText())) > 0;
        if (!existed)
        {
            SqliteDatabase.Execute(
                connection,
                transaction,
                "INSERT INTO logic_presets (name, name_key, form) VALUES ($name, $key, $form)",
                ("$name", name),
                ("$key", key),
                ("$form", form.GetRawText()));
        }
        return !existed;
    }

    /// <summary>Upper case is what .NET recommends for comparing without regard to case; it folds "ä" to "Ä", which SQLite would not.</summary>
    private static string Key(string name) => name.ToUpperInvariant();

    /// <summary>The form is parsed so that it goes out as JSON, not as a string of JSON.</summary>
    private static LogicPreset Read(SqliteDataReader reader)
    {
        using var form = JsonDocument.Parse(reader.GetString(2));
        return new LogicPreset(reader.GetInt64(0), reader.GetString(1), form.RootElement.Clone(), reader.GetString(3));
    }
}
