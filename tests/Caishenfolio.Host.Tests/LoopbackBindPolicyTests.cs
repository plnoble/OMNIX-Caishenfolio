using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Tests;

public class LoopbackBindPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void AcceptsLoopback(string host)
    {
        Assert.True(LoopbackBindPolicy.IsLoopbackHost(host));
        LoopbackBindPolicy.EnsureLoopback(host);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("example.com")]
    public void RejectsNonLoopback(string host)
    {
        Assert.False(LoopbackBindPolicy.IsLoopbackHost(host));
        Assert.Throws<InvalidOperationException>(() => LoopbackBindPolicy.EnsureLoopback(host));
    }

    [Fact]
    public void FlagsWildcardAsDenied()
    {
        Assert.True(LoopbackBindPolicy.IsDeniedWildcard("0.0.0.0"));
        Assert.True(LoopbackBindPolicy.IsDeniedWildcard("*"));
    }
}
