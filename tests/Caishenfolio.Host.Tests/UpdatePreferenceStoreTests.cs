using Caishenfolio.Host.Release;

namespace Caishenfolio.Host.Tests;

public class UpdatePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "caishenfolio_update_prefs", Guid.NewGuid().ToString("N"));

    private UpdatePreferenceStore NewStore() => new(_root);

    private static UpdateCheckResult Available(string latest = "0.11.0") => new()
    {
        Status = UpdateStatus.UpdateAvailable,
        CurrentVersion = "0.10.0",
        LatestVersion = latest,
        ReleaseUrl = "https://example.invalid",
        Message = "",
    };

    [Fact]
    public void NotifiesForANewerRelease()
    {
        Assert.True(new UpdatePreferences().ShouldNotify(Available()));
    }

    [Fact]
    public void StaysQuietForEverythingThatIsNotANewerRelease()
    {
        var preferences = new UpdatePreferences();

        foreach (var status in new[] { UpdateStatus.UpToDate, UpdateStatus.Ahead, UpdateStatus.Failed })
        {
            Assert.False(preferences.ShouldNotify(Available() with { Status = status }));
        }

        // Even "available" needs a version to show.
        Assert.False(preferences.ShouldNotify(Available() with { LatestVersion = null }));
    }

    [Fact]
    public void StaysQuietForAVersionTheUserIgnored()
    {
        var store = NewStore();
        store.IgnoreVersion("0.11.0");

        var preferences = store.Load();
        Assert.False(preferences.ShouldNotify(Available("0.11.0")));

        // A later release still gets through — ignoring is per version, not forever.
        Assert.True(preferences.ShouldNotify(Available("0.12.0")));
    }

    [Fact]
    public void PreferencesSurviveARestart()
    {
        NewStore().IgnoreVersion("0.11.0");
        Assert.Equal("0.11.0", NewStore().Load().IgnoredVersion);
    }

    [Fact]
    public void RecordsWhenTheLastCheckHappened()
    {
        var store = NewStore();
        var when = new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero);

        store.RecordCheck(when);

        Assert.Equal(when, DateTimeOffset.Parse(store.Load().LastCheckedUtc));
        // Recording a check must not clear an earlier decision.
        store.IgnoreVersion("0.11.0");
        store.RecordCheck(when.AddHours(1));
        Assert.Equal("0.11.0", store.Load().IgnoredVersion);
    }

    [Fact]
    public void AMissingOrCorruptFileFallsBackToDefaultsInsteadOfThrowing()
    {
        var store = NewStore();
        Assert.Equal("", store.Load().IgnoredVersion);

        Directory.CreateDirectory(_root);
        File.WriteAllText(store.FilePath, "{ not json at all");

        Assert.Equal("", store.Load().IgnoredVersion);
        Assert.True(store.Load().ShouldNotify(Available()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
