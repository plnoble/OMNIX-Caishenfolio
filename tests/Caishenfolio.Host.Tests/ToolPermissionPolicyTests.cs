using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Tests;

public class ToolPermissionPolicyTests
{
    [Fact]
    public void DenyByDefault()
    {
        var policy = new ToolPermissionPolicy();
        Assert.False(policy.IsAllowed(ToolCapability.ReadOnly));
        Assert.False(policy.IsAllowed(ToolCapability.Shell));
    }

    [Fact]
    public void EnableReadOnly_DoesNotEnableShell()
    {
        var policy = new ToolPermissionPolicy().Enable(ToolCapability.ReadOnly);
        Assert.True(policy.IsAllowed(ToolCapability.ReadOnly));
        Assert.False(policy.IsAllowed(ToolCapability.Shell));
        Assert.Throws<InvalidOperationException>(() => policy.EnsureAllowed(ToolCapability.Shell));
    }
}
