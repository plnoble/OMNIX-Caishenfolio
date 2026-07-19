namespace Caishenfolio.Host.Tasks;

public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkTask> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArtifactRecord> _artifacts = new(StringComparer.Ordinal);
    private readonly List<AuditEvent> _audits = new();

    public WorkTask CreateTask(WorkTaskKind kind, string title, IDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var task = new WorkTask
        {
            Id = $"task_{Guid.NewGuid():N}",
            Kind = kind,
            Title = title.Trim(),
        };
        if (metadata is not null)
        {
            foreach (var pair in metadata)
            {
                task.Metadata[pair.Key] = pair.Value;
            }
        }

        lock (_gate)
        {
            _tasks[task.Id] = task;
            _audits.Add(new AuditEvent
            {
                Id = $"audit_{Guid.NewGuid():N}",
                TaskId = task.Id,
                EventType = "task.created",
                Message = $"Created {kind} task.",
                Metadata = { ["title"] = task.Title },
            });
        }

        return task;
    }

    public WorkTask? GetTask(string taskId)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out var task) ? CloneTask(task) : null;
        }
    }

    public IReadOnlyList<WorkTask> ListTasks(WorkTaskKind? kind = null, WorkTaskStatus? status = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        lock (_gate)
        {
            return _tasks.Values
                .Where(task => kind is null || task.Kind == kind)
                .Where(task => status is null || task.Status == status)
                .OrderByDescending(task => task.UpdatedAt)
                .Take(limit)
                .Select(CloneTask)
                .ToArray();
        }
    }

    public WorkTask UpdateStatus(string taskId, WorkTaskStatus status, string? summary = null)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new KeyNotFoundException($"Unknown task '{taskId}'.");
            }

            task.Status = status;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            if (summary is not null)
            {
                task.Summary = summary;
            }

            _audits.Add(new AuditEvent
            {
                Id = $"audit_{Guid.NewGuid():N}",
                TaskId = taskId,
                EventType = "task.status",
                Message = $"Status -> {status}",
                Metadata =
                {
                    ["status"] = status.ToString(),
                },
            });

            return CloneTask(task);
        }
    }

    public ArtifactRecord AddArtifact(string taskId, string kind, string title, string? uriOrPayload = null, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new KeyNotFoundException($"Unknown task '{taskId}'.");
            }

            var artifact = new ArtifactRecord
            {
                Id = $"artifact_{Guid.NewGuid():N}",
                TaskId = taskId,
                Kind = kind.Trim(),
                Title = title.Trim(),
                UriOrPayload = uriOrPayload,
                ContentType = contentType,
            };
            _artifacts[artifact.Id] = artifact;
            task.ArtifactIds.Add(artifact.Id);
            task.UpdatedAt = DateTimeOffset.UtcNow;
            _audits.Add(new AuditEvent
            {
                Id = $"audit_{Guid.NewGuid():N}",
                TaskId = taskId,
                EventType = "artifact.created",
                Message = $"Artifact {artifact.Kind}: {artifact.Title}",
                Metadata = { ["artifact_id"] = artifact.Id },
            });
            return artifact;
        }
    }

    public ArtifactRecord? GetArtifact(string artifactId)
    {
        lock (_gate)
        {
            return _artifacts.TryGetValue(artifactId, out var artifact) ? CloneArtifact(artifact) : null;
        }
    }

    public IReadOnlyList<AuditEvent> ListAudit(string taskId, string? eventType = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_gate)
        {
            return _audits
                .Where(item => item.TaskId == taskId)
                .Where(item => eventType is null || string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Timestamp)
                .Take(limit)
                .Select(CloneAudit)
                .ToArray();
        }
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

    private static AuditEvent CloneAudit(AuditEvent source) =>
        new()
        {
            Id = source.Id,
            TaskId = source.TaskId,
            EventType = source.EventType,
            Message = source.Message,
            Timestamp = source.Timestamp,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
        };

    private static ArtifactRecord CloneArtifact(ArtifactRecord source) =>
        new()
        {
            Id = source.Id,
            TaskId = source.TaskId,
            Kind = source.Kind,
            Title = source.Title,
            ContentType = source.ContentType,
            UriOrPayload = source.UriOrPayload,
            CreatedAt = source.CreatedAt,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
        };
}
