namespace Caishenfolio.Host.Security;

public sealed class ToolPermissionPolicy
{
    private readonly HashSet<ToolCapability> _enabled = new();

    public ToolPermissionPolicy Enable(ToolCapability capability)
    {
        if (capability == ToolCapability.None)
        {
            return this;
        }

        foreach (ToolCapability flag in Enum.GetValues<ToolCapability>())
        {
            if (flag != ToolCapability.None && capability.HasFlag(flag))
            {
                _enabled.Add(flag);
            }
        }

        return this;
    }

    public bool IsAllowed(ToolCapability required)
    {
        if (required == ToolCapability.None)
        {
            return false;
        }

        foreach (ToolCapability flag in Enum.GetValues<ToolCapability>())
        {
            if (flag == ToolCapability.None)
            {
                continue;
            }

            if (required.HasFlag(flag) && !_enabled.Contains(flag))
            {
                return false;
            }
        }

        return true;
    }

    public void EnsureAllowed(ToolCapability required)
    {
        if (!IsAllowed(required))
        {
            throw new InvalidOperationException($"Capability '{required}' is not enabled by policy.");
        }
    }

    public IReadOnlyCollection<ToolCapability> EnabledCapabilities => _enabled.ToArray();
}
