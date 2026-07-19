namespace Caishenfolio.Host.Security;

public sealed class PathRootPolicy
{
    private readonly Dictionary<PathRootKind, string> _roots = new();

    public PathRootPolicy Register(PathRootKind kind, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (IsUnc(rootPath))
        {
            throw new ArgumentException("UNC paths are not allowed as path roots.", nameof(rootPath));
        }

        var full = Path.GetFullPath(rootPath.Trim());
        Directory.CreateDirectory(full);
        _roots[kind] = full;
        return this;
    }

    public string GetRoot(PathRootKind kind)
    {
        if (!_roots.TryGetValue(kind, out var root))
        {
            throw new InvalidOperationException($"Path root '{kind}' is not registered.");
        }

        return root;
    }

    public bool TryResolve(PathRootKind kind, string candidatePath, out string resolvedPath, out string reason)
    {
        resolvedPath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            reason = "Path is empty.";
            return false;
        }

        if (IsUnc(candidatePath))
        {
            reason = "UNC paths are rejected.";
            return false;
        }

        if (!_roots.TryGetValue(kind, out var root))
        {
            reason = $"Path root '{kind}' is not registered.";
            return false;
        }

        string fullCandidate;
        try
        {
            fullCandidate = Path.IsPathRooted(candidatePath)
                ? Path.GetFullPath(candidatePath)
                : Path.GetFullPath(Path.Combine(root, candidatePath));
        }
        catch (Exception ex)
        {
            reason = $"Path could not be resolved: {ex.Message}";
            return false;
        }

        if (!IsUnderRoot(root, fullCandidate))
        {
            reason = $"Path escapes allowed root '{kind}'.";
            return false;
        }

        resolvedPath = fullCandidate;
        return true;
    }

    public string Resolve(PathRootKind kind, string candidatePath)
    {
        if (TryResolve(kind, candidatePath, out var resolved, out var reason))
        {
            return resolved;
        }

        throw new InvalidOperationException(reason);
    }

    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsUnderRoot(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
