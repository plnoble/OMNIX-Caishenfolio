using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Tests;

public class SensitiveValueRedactorTests
{
    [Fact]
    public void RedactsAssignmentAndBearer()
    {
        var input = "api_key=abc123 token: zzz Bearer supersecretvalue";
        var output = SensitiveValueRedactor.RedactText(input);
        Assert.DoesNotContain("abc123", output);
        Assert.DoesNotContain("zzz", output);
        Assert.DoesNotContain("supersecretvalue", output);
        Assert.Contains("[REDACTED]", output);
    }

    [Fact]
    public void RedactsDictionaryByKey()
    {
        var data = new Dictionary<string, object?>
        {
            ["api_key"] = "secret-value",
            ["symbol"] = "SSE:600000",
        };

        var redacted = Assert.IsType<Dictionary<string, object?>>(SensitiveValueRedactor.RedactObject(data));
        Assert.Equal("[REDACTED]", redacted["api_key"]);
        Assert.Equal("SSE:600000", redacted["symbol"]);
    }
}
