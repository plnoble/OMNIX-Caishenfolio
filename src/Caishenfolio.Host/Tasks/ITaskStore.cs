namespace Caishenfolio.Host.Tasks;

public interface ITaskStore
{
    WorkTask CreateTask(WorkTaskKind kind, string title, IDictionary<string, string>? metadata = null);
    WorkTask? GetTask(string taskId);
    IReadOnlyList<WorkTask> ListTasks(WorkTaskKind? kind = null, WorkTaskStatus? status = null, int limit = 50);
    WorkTask UpdateStatus(string taskId, WorkTaskStatus status, string? summary = null);
    ArtifactRecord AddArtifact(
        string taskId,
        string kind,
        string title,
        string? uriOrPayload = null,
        string? contentType = null);
    ArtifactRecord? GetArtifact(string artifactId);
    IReadOnlyList<AuditEvent> ListAudit(string taskId, string? eventType = null, int limit = 50);
}
