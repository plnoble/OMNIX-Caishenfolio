using Caishenfolio.Host.Python;

namespace Caishenfolio.Host.Tasks;

/// <summary>
/// Mirrors research/task outcomes from Analytics Core into the Host durable store.
/// </summary>
public sealed class TaskMirrorService
{
    private readonly ITaskStore _store;

    public TaskMirrorService(ITaskStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public WorkTask MirrorResearchSnapshot(ResearchSnapshotResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var title = response.Task?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Research symbol snapshot";
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "analytics_core",
            ["command"] = "symbol_snapshot",
        };
        if (!string.IsNullOrWhiteSpace(response.Task?.Id))
        {
            metadata["core_task_id"] = response.Task!.Id;
        }

        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            metadata["error"] = response.Error!;
        }

        var task = _store.CreateTask(WorkTaskKind.Research, title, metadata);
        _store.UpdateStatus(task.Id, WorkTaskStatus.Running, "Mirroring Core research result.");

        if (response.Artifact is not null)
        {
            _store.AddArtifact(
                task.Id,
                response.Artifact.Kind,
                response.Artifact.Title,
                uriOrPayload: response.Artifact.UriOrPayload,
                contentType: response.Artifact.ContentType);
        }

        if (response.Ok)
        {
            return _store.UpdateStatus(
                task.Id,
                WorkTaskStatus.Succeeded,
                response.Task?.Summary ?? "Research succeeded.");
        }

        return _store.UpdateStatus(
            task.Id,
            WorkTaskStatus.Failed,
            response.Error ?? response.Task?.Summary ?? "Research failed.");
    }
}
