using Microsoft.Data.Sqlite;
using YueToLogic.Core.Arrangement;
using YueUI.Api.Logic;

namespace YueUI.Api.Data;

/// <summary>
/// The Logic page's instrument library and which track plays which, as yue-to-logic-pro kept them. A studio has one
/// set of hardware, so the instruments have no owner.
/// </summary>
public sealed class SqliteInstrumentStore(SqliteDatabase database)
{
    private const string Columns = "id, name, port, channel, kind, drum_kick, drum_snare, drum_closed_hihat, drum_open_hihat, drum_crash, drum_clap";

    /// <summary>SQLITE_CONSTRAINT_UNIQUE: the extended result code of a violated UNIQUE constraint.</summary>
    private const int UniqueConstraintViolated = 2067;

    /// <summary>All instruments, ordered by name.</summary>
    public IReadOnlyList<Instrument> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM instruments ORDER BY name COLLATE NOCASE, id";
        using var reader = command.ExecuteReader();
        var instruments = new List<Instrument>();
        while (reader.Read())
        {
            instruments.Add(Read(reader));
        }
        return instruments;
    }

    /// <exception cref="DuplicateInstrumentNameException">An instrument of that name exists already (case-insensitively).</exception>
    public Instrument Add(InstrumentValues values)
    {
        using var connection = database.Open();
        return Add(connection, null, values);
    }

    /// <returns>The changed instrument, or <c>null</c> when there is none with that id.</returns>
    /// <exception cref="DuplicateInstrumentNameException">Another instrument has that name already.</exception>
    public Instrument? Update(long id, InstrumentValues values)
    {
        using var connection = database.Open();
        return Update(connection, null, id, values) ? values.WithId(id) : null;
    }

    /// <summary>Removes the instrument and every assignment of a track to it.</summary>
    public bool Delete(long id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        // Spelled out rather than left to ON DELETE CASCADE, so it holds even if the pragma were ever missed.
        SqliteDatabase.Execute(connection, transaction, "DELETE FROM track_assignments WHERE instrument_id = $id", ("$id", id));
        var removed = SqliteDatabase.Execute(connection, transaction, "DELETE FROM instruments WHERE id = $id", ("$id", id));
        transaction.Commit();
        return removed > 0;
    }

    /// <summary>Track name → instrument id, for every track that has an instrument.</summary>
    public IReadOnlyDictionary<string, long> Assignments()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT track, instrument_id FROM track_assignments ORDER BY track";
        using var reader = command.ExecuteReader();
        var assignments = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            assignments[reader.GetString(0)] = reader.GetInt64(1);
        }
        return assignments;
    }

    /// <returns><c>false</c> when there is no instrument with that id; nothing is stored then.</returns>
    public bool Assign(string track, long instrumentId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var assigned = Assign(connection, transaction, track, instrumentId);
        transaction.Commit();
        return assigned;
    }

    public void Unassign(string track)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM track_assignments WHERE track = $track", ("$track", track));
    }

    /// <summary>
    /// Takes over the instruments and assignments of yue-to-logic-pro's server in one transaction. An instrument whose
    /// name exists already is updated rather than added twice, so running the import again changes nothing.
    /// </summary>
    /// <returns>How many instruments and assignments were taken.</returns>
    public (int Instruments, int Assignments) Import(IReadOnlyList<(long OldId, InstrumentValues Values)> instruments, IReadOnlyDictionary<string, long> assignments)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var ids = new Dictionary<long, long>();
        foreach (var (oldId, values) in instruments)
        {
            ids[oldId] = IdOf(connection, transaction, values.Name) is { } existing && Update(connection, transaction, existing, values)
                ? existing
                : Add(connection, transaction, values).Id;
        }
        var assigned = 0;
        foreach (var (track, oldId) in assignments)
        {
            if (ids.TryGetValue(oldId, out var id) && Assign(connection, transaction, track, id))
            {
                assigned++;
            }
        }
        transaction.Commit();
        return (ids.Count, assigned);
    }

    private static Instrument Add(SqliteConnection connection, SqliteTransaction? transaction, InstrumentValues values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO instruments (name, port, channel, kind, drum_kick, drum_snare, drum_closed_hihat, drum_open_hihat, drum_crash, drum_clap)
            VALUES ($name, $port, $channel, $kind, $kick, $snare, $closedHiHat, $openHiHat, $crash, $clap);
            SELECT last_insert_rowid();
            """;
        AddParameters(command, values);
        try
        {
            return values.WithId((long)command.ExecuteScalar()!);
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == UniqueConstraintViolated)
        {
            throw new DuplicateInstrumentNameException(values.Name);
        }
    }

    private static bool Update(SqliteConnection connection, SqliteTransaction? transaction, long id, InstrumentValues values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE instruments
            SET name = $name, port = $port, channel = $channel, kind = $kind,
                drum_kick = $kick, drum_snare = $snare, drum_closed_hihat = $closedHiHat, drum_open_hihat = $openHiHat,
                drum_crash = $crash, drum_clap = $clap,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        AddParameters(command, values);
        try
        {
            return command.ExecuteNonQuery() > 0;
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == UniqueConstraintViolated)
        {
            throw new DuplicateInstrumentNameException(values.Name);
        }
    }

    private static bool Assign(SqliteConnection connection, SqliteTransaction transaction, string track, long instrumentId)
    {
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM instruments WHERE id = $id";
        exists.Parameters.AddWithValue("$id", instrumentId);
        if (exists.ExecuteScalar() is null)
        {
            return false;
        }
        SqliteDatabase.Execute(
            connection,
            transaction,
            """
            INSERT INTO track_assignments (track, instrument_id) VALUES ($track, $instrument)
            ON CONFLICT (track) DO UPDATE SET instrument_id = excluded.instrument_id
            """,
            ("$track", track),
            ("$instrument", instrumentId));
        return true;
    }

    private static long? IdOf(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM instruments WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as long?;
    }

    /// <summary>The columns of an instrument as parameters; a synthesizer's drum notes are NULL.</summary>
    private static void AddParameters(SqliteCommand command, InstrumentValues values)
    {
        command.Parameters.AddWithValue("$name", values.Name);
        command.Parameters.AddWithValue("$port", values.Port);
        command.Parameters.AddWithValue("$channel", values.Channel);
        command.Parameters.AddWithValue("$kind", values.Kind.ToString());
        var drums = values.Drums;
        command.Parameters.AddWithValue("$kick", (object?)drums?.Kick ?? DBNull.Value);
        command.Parameters.AddWithValue("$snare", (object?)drums?.Snare ?? DBNull.Value);
        command.Parameters.AddWithValue("$closedHiHat", (object?)drums?.ClosedHiHat ?? DBNull.Value);
        command.Parameters.AddWithValue("$openHiHat", (object?)drums?.OpenHiHat ?? DBNull.Value);
        command.Parameters.AddWithValue("$crash", (object?)drums?.Crash ?? DBNull.Value);
        command.Parameters.AddWithValue("$clap", (object?)drums?.Clap ?? DBNull.Value);
    }

    /// <summary>A row in the column order of <see cref="Columns"/>; a kind the code does not know reads as a synthesizer.</summary>
    private static Instrument Read(SqliteDataReader reader)
    {
        var kind = Enum.TryParse<InstrumentKind>(reader.GetString(4), ignoreCase: true, out var parsed) ? parsed : InstrumentKind.Synth;
        var drums = kind != InstrumentKind.DrumMachine
            ? null
            : reader.IsDBNull(5)
                ? new DrumNotes()
                : new DrumNotes
                {
                    Kick = reader.GetInt32(5),
                    Snare = reader.GetInt32(6),
                    ClosedHiHat = reader.GetInt32(7),
                    OpenHiHat = reader.GetInt32(8),
                    Crash = reader.GetInt32(9),
                    Clap = reader.GetInt32(10),
                };
        return new Instrument(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), kind, drums);
    }
}
