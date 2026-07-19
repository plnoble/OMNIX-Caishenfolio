namespace Caishenfolio.Host.Tasks;

public enum WorkTaskStatus
{
    Created,
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
    Cancelled,
}

public enum WorkTaskKind
{
    System,
    MarketData,
    Research,
    Simulation,
    Report,
}

public sealed class WorkTask
{
    public required string Id { get; init; }
    public required WorkTaskKind Kind { get; init; }
    public required string Title { get; init; }
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Created;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Summary { get; set; }
    public List<string> ArtifactIds { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ArtifactRecord
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? ContentType { get; init; }
    public string? UriOrPayload { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AuditEvent
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
