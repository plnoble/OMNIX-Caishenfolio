using System.Net;

namespace Caishenfolio.Host.Security;

public static class LoopbackBindPolicy
{
    public static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var value = host.Trim().Trim('[', ']');
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        return IPAddress.IsLoopback(address);
    }

    public static void EnsureLoopback(string host)
    {
        if (!IsLoopbackHost(host))
        {
            throw new InvalidOperationException(
                $"Host '{host}' is not loopback. Managed Analytics Core must bind to loopback only.");
        }
    }

    public static bool IsDeniedWildcard(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var value = host.Trim();
        return value is "0.0.0.0" or "::" or "*";
    }
}
