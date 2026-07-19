using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Tests;

public class PathRootPolicyTests
{
    [Fact]
    public void Resolve_AllowsRelativePathInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "caishenfolio-path-tests", Guid.NewGuid().ToString("N"));
        var policy = new PathRootPolicy().Register(PathRootKind.Import, root);

        var resolved = policy.Resolve(PathRootKind.Import, "notes/a.txt");

        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("notes", "a.txt"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_RejectsTraversalOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "caishenfolio-path-tests", Guid.NewGuid().ToString("N"));
        var policy = new PathRootPolicy().Register(PathRootKind.Artifact, root);

        var ok = policy.TryResolve(PathRootKind.Artifact, @"..\outside.txt", out _, out var reason);

        Assert.False(ok);
        Assert.Contains("escapes", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_RejectsUncRoot()
    {
        var policy = new PathRootPolicy();
        Assert.Throws<ArgumentException>(() => policy.Register(PathRootKind.State, @"\\server\share"));
    }
}
