using Caishenfolio.Host.Python;
using Caishenfolio.Host.Tasks;

namespace Caishenfolio.Host.Tests;

public class TaskMirrorServiceTests
{
    [Fact]
    public void MirrorResearchSnapshot_SuccessCreatesArtifact()
    {
        var store = new InMemoryTaskStore();
        var mirror = new TaskMirrorService(store);
        var response = new ResearchSnapshotResponse
        {
            Ok = true,
            Disclaimer = "研究/模拟结论，非投资建议。",
            Task = new ResearchTaskDto
            {
                Id = "task_core1",
                Title = "Symbol snapshot SSE:600000",
                Status = "succeeded",
                Summary = "5 bars",
            },
            Artifact = new ResearchArtifactDto
            {
                Id = "artifact_core1",
                Kind = "research_snapshot",
                Title = "SSE:600000 fixture snapshot",
                ContentType = "application/json",
                UriOrPayload = "{\"bar_count\":5}",
            },
        };

        var mirrored = mirror.MirrorResearchSnapshot(response);

        Assert.Equal(WorkTaskStatus.Succeeded, mirrored.Status);
        Assert.Single(mirrored.ArtifactIds);
        Assert.Equal("task_core1", mirrored.Metadata["core_task_id"]);
        Assert.Contains(store.ListAudit(mirrored.Id), a => a.EventType == "artifact.created");
    }

    [Fact]
    public void MirrorResearchSnapshot_FailureMarksFailed()
    {
        var store = new InMemoryTaskStore();
        var mirror = new TaskMirrorService(store);
        var response = new ResearchSnapshotResponse
        {
            Ok = false,
            Error = "Symbol missing",
            Task = new ResearchTaskDto
            {
                Id = "task_core2",
                Title = "Symbol snapshot BAD",
                Status = "failed",
                Summary = "Symbol missing",
            },
        };

        var mirrored = mirror.MirrorResearchSnapshot(response);
        Assert.Equal(WorkTaskStatus.Failed, mirrored.Status);
        Assert.Equal("Symbol missing", mirrored.Summary);
    }
}
