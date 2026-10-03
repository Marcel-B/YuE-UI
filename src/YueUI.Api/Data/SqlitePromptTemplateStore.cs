using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YueUI.Api.Data;

/// <param name="Settings">What the browser saved from its form (style, seed, parameters); the server never reads it.</param>
public sealed record PromptTemplate(long Id, string Name, JsonElement Settings, string CreatedAt, string UpdatedAt);

public enum TemplateChange
{
    Changed,
    NotFound,
    NameTaken,
}

/// <summary>
/// Prompt templates: a style and the parameters of a song that turned out well, under a name to load into the form
/// again. Names are unique regardless of case (<c>name_key</c>, as in <see cref="SqliteSynthStore"/>), since two
/// templates a picker cannot tell apart are no use; unlike the synth sounds they are addressed by id, so a rename keeps
/// the template.
/// </summary>
public sealed class SqlitePromptTemplateStore(SqliteDatabase database, TimeProvider time)
{
    private const string Columns = "id, name, settings, created_at, updated_at";

    public IReadOnlyList<PromptTemplate> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM prompt_templates ORDER BY name_key, id";
        using var reader = command.ExecuteReader();
        var templates = new List<PromptTemplate>();
        while (reader.Read())
        {
            templates.Add(Read(reader));
        }
        return templates;
    }

    public PromptTemplate? Find(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM prompt_templates WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <returns>The new template, or <c>null</c> when one of that name exists.</returns>
    public PromptTemplate? Create(string name, JsonElement settings)
    {
        using var connection = database.Open();
        var now = time.GetUtcNow().ToString("O");
        try
        {
            SqliteDatabase.Execute(
                connection,
                null,
                "INSERT INTO prompt_templates (name, name_key, settings, created_at, updated_at) VALUES ($name, $key, $settings, $at, $at)",
                ("$name", name),
                ("$key", Key(name)),
                ("$settings", settings.GetRawText()),
                ("$at", now));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: the name is taken.
        {
            return null;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        return Find((long)command.ExecuteScalar()!);
    }

    /// <summary>Renames the template, replaces its settings, or both; what is <c>null</c> stays.</summary>
    public TemplateChange Update(long id, string? name, JsonElement? settings)
    {
        using var connection = database.Open();
        try
        {
            var changed = SqliteDatabase.Execute(
                connection,
                null,
                """
                UPDATE prompt_templates SET
                    name = coalesce($name, name),
                    name_key = coalesce($key, name_key),
                    settings = coalesce($settings, settings),
                    updated_at = $at
                WHERE id = $id
                """,
                ("$id", id),
                ("$name", (object?)name ?? DBNull.Value),
                ("$key", name is null ? DBNull.Value : (object)Key(name)),
                ("$settings", settings is { } json ? json.GetRawText() : (object)DBNull.Value),
                ("$at", time.GetUtcNow().ToString("O")));
            return changed > 0 ? TemplateChange.Changed : TemplateChange.NotFound;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            return TemplateChange.NameTaken;
        }
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(connection, null, "DELETE FROM prompt_templates WHERE id = $id", ("$id", id)) > 0;
    }

    private static string Key(string name) => name.ToUpperInvariant();

    /// <summary>The settings are parsed so that they go out as JSON, not as a string of JSON.</summary>
    private static PromptTemplate Read(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(2));
        return new PromptTemplate(
            reader.GetInt64(0),
            reader.GetString(1),
            document.RootElement.Clone(),
            reader.GetString(3),
            reader.GetString(4));
    }
}
