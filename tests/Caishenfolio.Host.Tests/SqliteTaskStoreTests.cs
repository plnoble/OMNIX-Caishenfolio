using Caishenfolio.Host.Tasks;

namespace Caishenfolio.Host.Tests;

public class SqliteTaskStoreTests
{
    [Fact]
    public void CreateUpdateAndAudit_PersistsAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "caishenfolio-tests", $"tasks-{Guid.NewGuid():N}.db");
        try
        {
            string taskId;
            string artifactId;
            using (var store = new SqliteTaskStore(path))
            {
                var task = store.CreateTask(WorkTaskKind.Research, "snapshot", new Dictionary<string, string>
                {
                    ["symbol"] = "NASDAQ:AAPL",
                });
                store.UpdateStatus(task.Id, WorkTaskStatus.Running);
                var artifact = store.AddArtifact(task.Id, "research_snapshot", "summary", uriOrPayload: "{\"ok\":true}");
                store.UpdateStatus(task.Id, WorkTaskStatus.Succeeded, "done");
                taskId = task.Id;
                artifactId = artifact.Id;
            }

            using (var reopened = new SqliteTaskStore(path))
            {
                var loaded = reopened.GetTask(taskId);
                Assert.NotNull(loaded);
                Assert.Equal(WorkTaskStatus.Succeeded, loaded!.Status);
                Assert.Contains(artifactId, loaded.ArtifactIds);
                Assert.Equal("NASDAQ:AAPL", loaded.Metadata["symbol"]);

                var artifact = reopened.GetArtifact(artifactId);
                Assert.NotNull(artifact);
                Assert.Equal("research_snapshot", artifact!.Kind);

                var audits = reopened.ListAudit(taskId);
                Assert.True(audits.Count >= 3);
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void UnderStateRoot_CreatesDatabaseFile()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "caishenfolio-tests", $"state-{Guid.NewGuid():N}");
        try
        {
            using (var store = SqliteTaskStore.UnderStateRoot(stateRoot))
            {
                store.CreateTask(WorkTaskKind.System, "boot");
                Assert.True(File.Exists(Path.Combine(stateRoot, "tasks.db")));
            }

            Assert.True(File.Exists(Path.Combine(stateRoot, "tasks.db")));
        }
        finally
        {
            TryDeleteDirectory(stateRoot);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup in CI temp dirs.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup in CI temp dirs.
        }
    }
}
