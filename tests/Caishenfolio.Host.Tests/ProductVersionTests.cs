using Caishenfolio.Host;

namespace Caishenfolio.Host.Tests;

/// <summary>
/// Guards the single source of truth. The version used to live in four places and had already
/// drifted to four different numbers; these assertions turn any future drift into a red test.
/// </summary>
public class ProductVersionTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VERSION")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string ReadRootFile(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), name)).Trim();

    [Fact]
    public void AssemblyVersionMatchesTheVersionFile()
    {
        Assert.Equal(ReadRootFile("VERSION"), ProductInfo.Version);
    }

    [Fact]
    public void AssemblyPhaseMatchesThePhaseFile()
    {
        Assert.Equal(ReadRootFile("PHASE"), ProductInfo.Phase);
    }

    [Fact]
    public void PythonCoreDeclaresTheSameVersionAndPhase()
    {
        var initPath = Path.Combine(RepoRoot(), "python", "caishenfolio_core", "__init__.py");
        var text = File.ReadAllText(initPath);

        Assert.Contains($"__version__ = \"{ReadRootFile("VERSION")}\"", text);
        Assert.Contains($"PRODUCT_PHASE = \"{ReadRootFile("PHASE")}\"", text);
    }

    [Fact]
    public void PythonPackageMetadataDeclaresTheSameVersion()
    {
        var pyproject = File.ReadAllText(Path.Combine(RepoRoot(), "python", "pyproject.toml"));
        Assert.Contains($"version = \"{ReadRootFile("VERSION")}\"", pyproject);
    }

    [Theory]
    [InlineData("VERSION")]
    [InlineData("PHASE")]
    public void RootMetadataFilesAreASingleTrimmedLine(string name)
    {
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), name));
        Assert.Single(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotEmpty(raw.Trim());
    }

    [Fact]
    public void VersionIsThreePartSemver()
    {
        var parts = ProductInfo.Version.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _), $"'{part}' 不是数字。"));
    }
}
