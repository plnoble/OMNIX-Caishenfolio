using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Caishenfolio.Host.Tasks;

/// <summary>
/// Durable task/artifact/audit mirror under the Host State path root.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore, IDisposable
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public SqliteTaskStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var full = Path.GetFullPath(databasePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        Initialize();
    }

    public static SqliteTaskStore UnderStateRoot(string stateRootDirectory, string fileName = "tasks.db")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootDirectory);
        var path = Path.Combine(Path.GetFullPath(stateRootDirectory), fileName);
        return new SqliteTaskStore(path);
    }

    public WorkTask CreateTask(WorkTaskKind kind, string title, IDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var now = DateTimeOffset.UtcNow;
        var task = new WorkTask
        {
            Id = $"task_{Guid.NewGuid():N}",
            Kind = kind,
            Title = title.Trim(),
            Status = WorkTaskStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (metadata is not null)
        {
            foreach (var pair in metadata)
            {
                task.Metadata[pair.Key] = pair.Value;
            }
        }

        var audit = new AuditEvent
        {
            Id = $"audit_{Guid.NewGuid():N}",
            TaskId = task.Id,
            EventType = "task.created",
            Message = $"Created {kind} task.",
            Timestamp = now,
            Metadata = { ["title"] = task.Title },
        };

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            InsertTask(connection, tx, task);
            InsertAudit(connection, tx, audit);
            tx.Commit();
        }

        return CloneTask(task);
    }

    public WorkTask? GetTask(string taskId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, title, status, created_at, updated_at, summary, artifact_ids_json, metadata_json
                FROM tasks WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", taskId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTask(reader) : null;
        }
    }

    public IReadOnlyList<WorkTask> ListTasks(WorkTaskKind? kind = null, WorkTaskStatus? status = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, title, status, created_at, updated_at, summary, artifact_ids_json, metadata_json
                FROM tasks
                WHERE ($kind IS NULL OR kind = $kind)
                  AND ($status IS NULL OR status = $status)
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : kind.Value.ToString());
            command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : status.Value.ToString());
            command.Parameters.AddWithValue("$limit", limit);

            var items = new List<WorkTask>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadTask(reader));
            }

            return items;
        }
    }

    public WorkTask UpdateStatus(string taskId, WorkTaskStatus status, string? summary = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var task = GetTaskLocked(connection, tx, taskId)
                ?? throw new KeyNotFoundException($"Unknown task '{taskId}'.");

            task.Status = status;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            if (summary is not null)
            {
                task.Summary = summary;
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE tasks
                    SET status = $status, updated_at = $updated_at, summary = $summary
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$status", task.Status.ToString());
                update.Parameters.AddWithValue("$updated_at", task.UpdatedAt.ToString("O"));
                update.Parameters.AddWithValue("$summary", (object?)task.Summary ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", task.Id);
                update.ExecuteNonQuery();
            }

            var audit = new AuditEvent
            {
                Id = $"audit_{Guid.NewGuid():N}",
                TaskId = taskId,
                EventType = "task.status",
                Message = $"Status -> {status}",
                Timestamp = DateTimeOffset.UtcNow,
                Metadata = { ["status"] = status.ToString() },
            };
            InsertAudit(connection, tx, audit);
            tx.Commit();
            return CloneTask(task);
        }
    }

    public ArtifactRecord AddArtifact(
        string taskId,
        string kind,
        string title,
        string? uriOrPayload = null,
        string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var task = GetTaskLocked(connection, tx, taskId)
                ?? throw new KeyNotFoundException($"Unknown task '{taskId}'.");

            var artifact = new ArtifactRecord
            {
                Id = $"artifact_{Guid.NewGuid():N}",
                TaskId = taskId,
                Kind = kind.Trim(),
                Title = title.Trim(),
                UriOrPayload = uriOrPayload,
                ContentType = contentType,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO artifacts
                    (id, task_id, kind, title, content_type, uri_or_payload, created_at, metadata_json)
                    VALUES
                    ($id, $task_id, $kind, $title, $content_type, $uri_or_payload, $created_at, $metadata_json);
                    """;
                insert.Parameters.AddWithValue("$id", artifact.Id);
                insert.Parameters.AddWithValue("$task_id", artifact.TaskId);
                insert.Parameters.AddWithValue("$kind", artifact.Kind);
                insert.Parameters.AddWithValue("$title", artifact.Title);
                insert.Parameters.AddWithValue("$content_type", (object?)artifact.ContentType ?? DBNull.Value);
                insert.Parameters.AddWithValue("$uri_or_payload", (object?)artifact.UriOrPayload ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created_at", artifact.CreatedAt.ToString("O"));
                insert.Parameters.AddWithValue("$metadata_json", SerializeMetadata(artifact.Metadata));
                insert.ExecuteNonQuery();
            }

            task.ArtifactIds.Add(artifact.Id);
            task.UpdatedAt = DateTimeOffset.UtcNow;
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE tasks
                    SET artifact_ids_json = $artifact_ids_json, updated_at = $updated_at
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$artifact_ids_json", JsonSerializer.Serialize(task.ArtifactIds));
                update.Parameters.AddWithValue("$updated_at", task.UpdatedAt.ToString("O"));
                update.Parameters.AddWithValue("$id", task.Id);
                update.ExecuteNonQuery();
            }

            var audit = new AuditEvent
            {
                Id = $"audit_{Guid.NewGuid():N}",
                TaskId = taskId,
                EventType = "artifact.created",
                Message = $"Artifact {artifact.Kind}: {artifact.Title}",
                Timestamp = DateTimeOffset.UtcNow,
                Metadata = { ["artifact_id"] = artifact.Id },
            };
            InsertAudit(connection, tx, audit);
            tx.Commit();
            return artifact;
        }
    }

    public ArtifactRecord? GetArtifact(string artifactId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, task_id, kind, title, content_type, uri_or_payload, created_at, metadata_json
                FROM artifacts WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", artifactId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ArtifactRecord
            {
                Id = reader.GetString(0),
                TaskId = reader.GetString(1),
                Kind = reader.GetString(2),
                Title = reader.GetString(3),
                ContentType = reader.IsDBNull(4) ? null : reader.GetString(4),
                UriOrPayload = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
                Metadata = DeserializeMetadata(reader.GetString(7)),
            };
        }
    }

    public IReadOnlyList<AuditEvent> ListAudit(string taskId, string? eventType = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, task_id, event_type, message, timestamp, metadata_json
                FROM audits
                WHERE task_id = $task_id
                  AND ($event_type IS NULL OR event_type = $event_type)
                ORDER BY timestamp DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$task_id", taskId);
            command.Parameters.AddWithValue("$event_type", (object?)eventType ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);

            var items = new List<AuditEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new AuditEvent
                {
                    Id = reader.GetString(0),
                    TaskId = reader.GetString(1),
                    EventType = reader.GetString(2),
                    Message = reader.GetString(3),
                    Timestamp = DateTimeOffset.Parse(reader.GetString(4)),
                    Metadata = DeserializeMetadata(reader.GetString(5)),
                });
            }

            return items;
        }
    }

    public void Dispose()
    {
        // Ensure native handles release the database file (important for tests / app shutdown).
        SqliteConnection.ClearAllPools();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                summary TEXT NULL,
                artifact_ids_json TEXT NOT NULL,
                metadata_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifacts (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                content_type TEXT NULL,
                uri_or_payload TEXT NULL,
                created_at TEXT NOT NULL,
                metadata_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audits (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                metadata_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_updated ON tasks(updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_audits_task ON audits(task_id, timestamp DESC);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static WorkTask? GetTaskLocked(SqliteConnection connection, SqliteTransaction tx, string taskId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT id, kind, title, status, created_at, updated_at, summary, artifact_ids_json, metadata_json
            FROM tasks WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    private static void InsertTask(SqliteConnection connection, SqliteTransaction tx, WorkTask task)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO tasks
            (id, kind, title, status, created_at, updated_at, summary, artifact_ids_json, metadata_json)
            VALUES
            ($id, $kind, $title, $status, $created_at, $updated_at, $summary, $artifact_ids_json, $metadata_json);
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$kind", task.Kind.ToString());
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$created_at", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", task.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$summary", (object?)task.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifact_ids_json", JsonSerializer.Serialize(task.ArtifactIds));
        command.Parameters.AddWithValue("$metadata_json", SerializeMetadata(task.Metadata));
        command.ExecuteNonQuery();
    }

    private static void InsertAudit(SqliteConnection connection, SqliteTransaction tx, AuditEvent audit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO audits
            (id, task_id, event_type, message, timestamp, metadata_json)
            VALUES
            ($id, $task_id, $event_type, $message, $timestamp, $metadata_json);
            """;
        command.Parameters.AddWithValue("$id", audit.Id);
        command.Parameters.AddWithValue("$task_id", audit.TaskId);
        command.Parameters.AddWithValue("$event_type", audit.EventType);
        command.Parameters.AddWithValue("$message", audit.Message);
        command.Parameters.AddWithValue("$timestamp", audit.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$metadata_json", SerializeMetadata(audit.Metadata));
        command.ExecuteNonQuery();
    }

    private static WorkTask ReadTask(SqliteDataReader reader)
    {
        var artifactIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? new List<string>();
        return new WorkTask
        {
            Id = reader.GetString(0),
            Kind = Enum.Parse<WorkTaskKind>(reader.GetString(1)),
            Title = reader.GetString(2),
            Status = Enum.Parse<WorkTaskStatus>(reader.GetString(3)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
            ArtifactIds = artifactIds,
            Metadata = DeserializeMetadata(reader.GetString(8)),
        };
    }

    private static string SerializeMetadata(IDictionary<string, string> metadata) =>
        JsonSerializer.Serialize(metadata);

    private static Dictionary<string, string> DeserializeMetadata(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private static WorkTask CloneTask(WorkTask source) =>
        new()
        {
            Id = source.Id,
            Kind = source.Kind,
            Title = source.Title,
            Status = source.Status,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            Summary = source.Summary,
            ArtifactIds = source.ArtifactIds.ToList(),
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
        };
}
