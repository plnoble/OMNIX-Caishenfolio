using System.Diagnostics;

namespace Caishenfolio.Host.Python;

/// <summary>
/// Ensures free market Python packages are importable; installs via pip if missing.
/// </summary>
public static class PythonDependencyBootstrap
{
    private static readonly string[] RequiredModules = ["akshare", "pandas", "yfinance"];

    public static async Task<BootstrapResult> EnsureMarketDependenciesAsync(
        string pythonExecutable,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);

        progress?.Report("正在检测 Python 行情依赖（akshare / pandas / yfinance）…");
        if (await CanImportAllAsync(pythonExecutable, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report("行情依赖已就绪。");
            return BootstrapResult.AlreadySatisfied();
        }

        progress?.Report("缺少依赖，正在自动安装（首次可能需几分钟，请保持网络畅通）…");
        progress?.Report("pip install akshare pandas yfinance");

        var install = await RunAsync(
            pythonExecutable,
            "-m pip install --upgrade akshare pandas yfinance",
            cancellationToken).ConfigureAwait(false);

        if (install.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(install.StdErr) ? install.StdOut : install.StdErr;
            return BootstrapResult.Failed(
                "自动安装依赖失败。请手动执行：python -m pip install akshare pandas yfinance\n" + detail);
        }

        if (!await CanImportAllAsync(pythonExecutable, cancellationToken).ConfigureAwait(false))
        {
            return BootstrapResult.Failed(
                "pip 已执行但模块仍不可 import。请检查是否装到了当前 python 环境。");
        }

        progress?.Report("依赖安装完成。");
        return BootstrapResult.Installed();
    }

    private static async Task<bool> CanImportAllAsync(string python, CancellationToken ct)
    {
        // Import each module separately for clearer failures.
        foreach (var module in RequiredModules)
        {
            var check = await RunAsync(python, $"-c \"import {module}\"", ct).ConfigureAwait(false);
            if (check.ExitCode != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程：{fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}

public sealed class BootstrapResult
{
    public bool Ok { get; init; }
    public bool DidInstall { get; init; }
    public string Message { get; init; } = "";

    public static BootstrapResult AlreadySatisfied() =>
        new() { Ok = true, DidInstall = false, Message = "依赖已存在" };

    public static BootstrapResult Installed() =>
        new() { Ok = true, DidInstall = true, Message = "依赖已安装" };

    public static BootstrapResult Failed(string message) =>
        new() { Ok = false, DidInstall = false, Message = message };
}
