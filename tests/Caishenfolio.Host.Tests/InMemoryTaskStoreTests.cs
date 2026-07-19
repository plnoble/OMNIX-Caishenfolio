using Caishenfolio.Host.Tasks;

namespace Caishenfolio.Host.Tests;

public class InMemoryTaskStoreTests
{
    [Fact]
    public void CreateUpdateAndAudit_RoundTrip()
    {
        var store = new InMemoryTaskStore();
        var task = store.CreateTask(WorkTaskKind.MarketData, "Load bars", new Dictionary<string, string>
        {
            ["symbol"] = "SSE:600000",
        });

        store.UpdateStatus(task.Id, WorkTaskStatus.Running);
        var artifact = store.AddArtifact(task.Id, "bars", "OHLCV fixture", uriOrPayload: "memory://bars");
        var finished = store.UpdateStatus(task.Id, WorkTaskStatus.Succeeded, "ok");
        var audits = store.ListAudit(task.Id);

        Assert.Equal(WorkTaskStatus.Succeeded, finished.Status);
        Assert.Contains(artifact.Id, finished.ArtifactIds);
        Assert.True(audits.Count >= 3);
        Assert.Contains(audits, item => item.EventType == "task.created");
        Assert.Contains(audits, item => item.EventType == "artifact.created");
    }

    [Fact]
    public void ListTasks_FiltersByKind()
    {
        var store = new InMemoryTaskStore();
        store.CreateTask(WorkTaskKind.System, "sys");
        store.CreateTask(WorkTaskKind.Research, "research");

        var research = store.ListTasks(kind: WorkTaskKind.Research);
        Assert.Single(research);
        Assert.Equal(WorkTaskKind.Research, research[0].Kind);
    }
}
