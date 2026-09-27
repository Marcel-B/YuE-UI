using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YueUI.Api.Queue;

namespace YueUI.Api.Data;

/// <summary>
/// The jobs waiting in <see cref="JobQueue"/>, so that they survive a restart or a deploy. A job leaves the table
/// when it starts: from then on the worker or the lyrics writer owns it.
/// </summary>
/// <remarks>
/// The job as the browsers see it and the payload (the worker command or the lyrics request, with a photo up to a
/// few MB) are stored as JSON: nothing queries their fields, and new ones need no migration.
/// </remarks>
public sealed class SqliteJobStore(SqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The waiting jobs in their order.</summary>
    public IReadOnlyList<(QueuedJob Job, JsonObject Payload)> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job, payload FROM queued_jobs ORDER BY position";
        using var reader = command.ExecuteReader();
        var jobs = new List<(QueuedJob, JsonObject)>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<QueuedJob>(reader.GetString(0), Json) is { } job
                && JsonNode.Parse(reader.GetString(1)) is JsonObject payload)
            {
                jobs.Add((job, payload));
            }
        }
        return jobs;
    }

    /// <summary>Appends the job behind every other.</summary>
    public void Add(QueuedJob job, JsonObject payload)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(
            connection,
            null,
            """
            INSERT INTO queued_jobs (id, position, kind, job, payload)
            VALUES ($id, (SELECT coalesce(max(position), 0) + 1 FROM queued_jobs), $kind, $job, $payload)
            """,
            ("$id", job.Id),
            ("$kind", JsonNamingPolicy.CamelCase.ConvertName(job.Kind.ToString())),
            ("$job", JsonSerializer.Serialize(job, Json)),
            ("$payload", payload.ToJsonString()));
    }

    public void Remove(string id)
    {
        using var connection = database.Open();
        SqliteDatabase.Execute(connection, null, "DELETE FROM queued_jobs WHERE id = $id", ("$id", id));
    }

    /// <summary>Numbers the jobs in the given order; ids no longer in the table are skipped.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        for (var position = 0; position < ids.Count; position++)
        {
            SqliteDatabase.Execute(
                connection,
                transaction,
                "UPDATE queued_jobs SET position = $position WHERE id = $id",
                ("$position", position + 1),
                ("$id", ids[position]));
        }
        transaction.Commit();
    }
}
