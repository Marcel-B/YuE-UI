using System.Text.Json;
using Microsoft.Data.Sqlite;
using YueUI.Api.Logic;

namespace YueUI.Api.Data;

/// <summary>
/// The Logic page's browser synthesizer: named sounds (<c>synth_presets</c>, names matched through <c>name_key</c> as in
/// <see cref="SqliteLogicPresetStore"/>) and the sound each track plays (<c>track_synths</c>, by track name like the
/// instrument assignments). A track keeps its own copy of a sound, so changing a preset changes no track.
/// </summary>
public sealed class SqliteSynthStore(SqliteDatabase database)
{
    public IReadOnlyList<SynthPreset> ListPresets()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, patch, updated_at FROM synth_presets ORDER BY name_key, id";
        using var reader = command.ExecuteReader();
        var presets = new List<SynthPreset>();
        while (reader.Read())
        {
            presets.Add(new SynthPreset(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetString(3)));
        }
        return presets;
    }

    /// <summary>Adds the sound, or replaces the one of that name (case-insensitively); the spelling follows the latest save.</summary>
    /// <returns>The sound as stored, and whether it was new.</returns>
    public (SynthPreset Preset, bool Created) SavePreset(string name, JsonElement patch)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var key = Key(name);
        var existed = SqliteDatabase.Execute(
            connection,
            transaction,
            """
            UPDATE synth_presets SET name = $name, patch = $patch, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE name_key = $key
            """,
            ("$key", key),
            ("$name", name),
            ("$patch", patch.GetRawText())) > 0;
        if (!existed)
        {
            SqliteDatabase.Execute(
                connection,
                transaction,
                "INSERT INTO synth_presets (name, name_key, patch) VALUES ($name, $key, $patch)",
                ("$name", name),
                ("$key", key),
                ("$patch", patch.GetRawText()));
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, patch, updated_at FROM synth_presets WHERE name_key = $key";
        command.Parameters.AddWithValue("$key", key);
        SynthPreset preset;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            preset = new SynthPreset(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2)), reader.GetString(3));
        }
        transaction.Commit();
        return (preset, !existed);
    }

    public bool DeletePreset(string name)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(connection, null, "DELETE FROM synth_presets WHERE name_key = $key", ("$key", Key(name))) > 0;
    }

    public IReadOnlyList<TrackSynth> ListTracks()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT track, patch, preset, updated_at FROM track_synths ORDER BY track";
        using var reader = command.ExecuteReader();
        var tracks = new List<TrackSynth>();
        while (reader.Read())
        {
            tracks.Add(new TrackSynth(
                reader.GetString(0),
                Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        }
        return tracks;
    }

    public TrackSynth SetTrack(string track, JsonElement patch, string? preset)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO track_synths (track, patch, preset) VALUES ($track, $patch, $preset)
            ON CONFLICT (track) DO UPDATE SET
                patch = excluded.patch, preset = excluded.preset, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            """,
            ("$track", track),
            ("$patch", patch.GetRawText()),
            ("$preset", (object?)preset ?? DBNull.Value));
        return ListTracks().First(entry => string.Equals(entry.Track, track, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gives the track back its default sound.</summary>
    public bool RemoveTrack(string track)
    {
        using var connection = database.Open();
        return SqliteDatabase.Execute(connection, null, "DELETE FROM track_synths WHERE track = $track", ("$track", track)) > 0;
    }

    /// <summary>The mixer's settings, or <c>null</c> before they were first saved.</summary>
    public MixerSettings GetMixer()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT settings, updated_at FROM logic_mixer WHERE id = 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new MixerSettings(Parse(reader.GetString(0)), reader.GetString(1)) : new MixerSettings(null, null);
    }

    public MixerSettings SetMixer(JsonElement settings)
    {
        using (var connection = database.Open())
        {
            SqliteDatabase.Execute(
                connection,
                null,
                """
                INSERT INTO logic_mixer (id, settings) VALUES (1, $settings)
                ON CONFLICT (id) DO UPDATE SET settings = excluded.settings, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                """,
                ("$settings", settings.GetRawText()));
        }
        return GetMixer();
    }

    private static string Key(string name) => name.ToUpperInvariant();

    /// <summary>The patch is parsed so that it goes out as JSON, not as a string of JSON.</summary>
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
