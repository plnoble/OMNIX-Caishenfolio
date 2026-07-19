using System.Diagnostics;
using Caishenfolio.Host.MarketData;
using Caishenfolio.Host.Security;

namespace Caishenfolio.Host.Python;

public sealed class AnalyticsCoreProcessBroker : IDisposable
{
    private Process? _process;

    public int Port { get; }
    public string Host { get; }
    public bool IsRunning => _process is { HasExited: false };

    public AnalyticsCoreProcessBroker(string host = "127.0.0.1", int port = 8765)
    {
        LoopbackBindPolicy.EnsureLoopback(host);
        if (LoopbackBindPolicy.IsDeniedWildcard(host))
        {
            throw new InvalidOperationException($"Host '{host}' is not allowed.");
        }

        Host = host;
        Port = port;
    }

    public Uri BaseAddress => new($"http://{Host}:{Port}/");

    public void Start(
        string pythonExecutable,
        string repoRoot,
        MarketCredentialsStore? credentialsStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        if (IsRunning)
        {
            return;
        }

        var pythonRoot = Path.Combine(repoRoot, "python");
        if (!Directory.Exists(pythonRoot))
        {
            throw new DirectoryNotFoundException($"Python package root not found: {pythonRoot}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            Arguments = $"-m caishenfolio_core.server --host {Host} --port {Port}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["PYTHONPATH"] = pythonRoot;
        startInfo.Environment["CAISHENFOLIO_BIND_HOST"] = Host;
        startInfo.Environment["CAISHENFOLIO_BIND_PORT"] = Port.ToString();

        if (credentialsStore is not null)
        {
            var bag = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            credentialsStore.ApplyToEnvironment(bag);
            foreach (var pair in bag)
            {
                if (pair.Value is not null)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            // Bars cache path comes from credentials ApplyToEnvironment (user-selectable).
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAISHENFOLIO_MARKET_PROVIDER")))
            {
                startInfo.Environment["CAISHENFOLIO_MARKET_PROVIDER"] = "auto";
            }

            var trustEnv = Environment.GetEnvironmentVariable("CAISHENFOLIO_HTTP_TRUST_ENV");
            if (!string.IsNullOrWhiteSpace(trustEnv))
            {
                startInfo.Environment["CAISHENFOLIO_HTTP_TRUST_ENV"] = trustEnv;
            }
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动分析核心进程。");
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();
}
