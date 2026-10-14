using System.Diagnostics;
using System.Net.Sockets;
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

        var providerArgument = "";
        if (TestIsolation.TryGetForcedProvider(out var forcedOnCommand))
        {
            providerArgument = $" --provider {forcedOnCommand}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            Arguments = $"-m caishenfolio_core.server --host {Host} --port {Port}{providerArgument}",
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

            // Test isolation (recheck-5 R5-01): credentials legitimately override the
            // inherited provider for production. A force-provider only applies when the
            // same process also has an explicit isolated test state root, and only for
            // the allowlisted fixture value. A stray variable must not change a real boot.
            if (TestIsolation.TryGetForcedProvider(out var forcedProvider))
            {
                startInfo.Environment["CAISHENFOLIO_MARKET_PROVIDER"] = forcedProvider;
            }
        }
        else
        {
            // No credentials store: keep the inherited provider. Do not let a test-only
            // variable rewrite a production-shaped launch that never opted into isolation.
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

        if (IsLoopbackPortInUse(Port))
        {
            throw new InvalidOperationException(
                $"分析核心端口 {Host}:{Port} 已被占用。不会认领已有进程；请停止占用者或使用隔离端口。");
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动分析核心进程。");
    }

    /// <summary>True when something is already accepting connections on this loopback port.</summary>
    public static bool IsLoopbackPortInUse(int port)
    {
        try
        {
            using var probe = new TcpClient();
            var task = probe.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(250)) && probe.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or IOException)
        {
            return false;
        }
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
