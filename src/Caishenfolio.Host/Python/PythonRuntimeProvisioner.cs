using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Caishenfolio.Host.Python;

public sealed record ProcessCommand(string FileName, string Arguments, string WorkingDirectory);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Process execution seam, so provisioning can be tested without spawning anything.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default);
}

public sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessCommand command, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动进程：{command.FileName}");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

public sealed record PythonRuntimeOptions
{
    /// <summary>Where the venv and the provisioning log live — the Host State root.</summary>
    public required string StateRoot { get; init; }
    /// <summary>Repository <c>python/</c> directory holding <c>pyproject.toml</c>.</summary>
    public required string PythonProjectDirectory { get; init; }
    public string VenvDirectory { get; init; } = "";
    public string UvExecutable { get; init; } = "uv";
    public string PythonVersion { get; init; } = "3.12";
    public string DependencyManifest { get; init; } = "pyproject.toml";
    /// <summary>Extras installed with the package; the market providers are optional deps.</summary>
    public string Extras { get; init; } = "market";
    public string MarkerFileName { get; init; } = ".omnix-runtime.hash";
    public string LogFileName { get; init; } = "python-runtime.log";

    public string ResolvedVenvDirectory =>
        string.IsNullOrWhiteSpace(VenvDirectory) ? Path.Combine(StateRoot, ".venv") : VenvDirectory;

    public string VenvInterpreter =>
        Path.Combine(ResolvedVenvDirectory, "Scripts", "python.exe");

    public string MarkerPath => Path.Combine(ResolvedVenvDirectory, MarkerFileName);

    public string ManifestPath => Path.Combine(PythonProjectDirectory, DependencyManifest);

    public string LogPath => Path.Combine(StateRoot, LogFileName);
}

public sealed record PythonRuntimeStatus
{
    public required bool UvAvailable { get; init; }
    public required bool VenvExists { get; init; }
    /// <summary>The installed dependency set matches the current <c>pyproject.toml</c>.</summary>
    public required bool DependenciesCurrent { get; init; }
    public string? ManifestHash { get; init; }
    public string? MarkerHash { get; init; }
    /// <summary>Interpreter to launch the Analytics Core with; null when nothing usable was found.</summary>
    public string? Interpreter { get; init; }
    public required string LogPath { get; init; }
    public required string Summary { get; init; }

    /// <summary>The managed venv is present and its dependencies match the manifest.</summary>
    public bool IsManagedAndCurrent => VenvExists && DependenciesCurrent;

    /// <summary>Something can run the core, managed or not.</summary>
    public bool CanRun => !string.IsNullOrEmpty(Interpreter);
}

/// <summary>
/// Provisions a private Python environment for the Analytics Core.
///
/// Why this exists: a user who installs the MSI has no reason to have Python, and the previous
/// bootstrap installed packages into whatever interpreter happened to be on PATH — polluting a
/// system environment the app does not own. This creates a venv under the Host State root and
/// stamps it with a hash of <c>pyproject.toml</c>, so dependencies are reinstalled exactly when
/// the manifest changes and never on every launch.
///
/// It degrades rather than blocks: when <c>uv</c> is absent, a usable system interpreter is
/// reported instead, so a developer machine keeps working.
/// </summary>
public sealed class PythonRuntimeProvisioner(
    PythonRuntimeOptions options,
    IProcessRunner? runner = null,
    string systemInterpreter = "python")
{
    private readonly IProcessRunner _runner = runner ?? new DefaultProcessRunner();

    public PythonRuntimeOptions Options => options;

    public async Task<PythonRuntimeStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.StateRoot);

        var uvAvailable = await ProbeAsync(options.UvExecutable, "--version", cancellationToken).ConfigureAwait(false);
        var venvExists = File.Exists(options.VenvInterpreter);

        var manifestHash = File.Exists(options.ManifestPath)
            ? await HashFileAsync(options.ManifestPath, cancellationToken).ConfigureAwait(false)
            : null;
        var markerHash = File.Exists(options.MarkerPath)
            ? (await File.ReadAllTextAsync(options.MarkerPath, cancellationToken).ConfigureAwait(false)).Trim()
            : null;
        var current = manifestHash is not null && manifestHash == markerHash;

        string? interpreter = null;
        if (venvExists)
        {
            interpreter = options.VenvInterpreter;
        }
        else if (await ProbeAsync(systemInterpreter, "--version", cancellationToken).ConfigureAwait(false))
        {
            interpreter = systemInterpreter;
        }

        var summary = BuildSummary(uvAvailable, venvExists, current, interpreter);
        await LogAsync(
            $"inspect uv={uvAvailable} venv={venvExists} current={current} interpreter={interpreter ?? "-"}",
            cancellationToken).ConfigureAwait(false);

        return new PythonRuntimeStatus
        {
            UvAvailable = uvAvailable,
            VenvExists = venvExists,
            DependenciesCurrent = current,
            ManifestHash = manifestHash,
            MarkerHash = markerHash,
            Interpreter = interpreter,
            LogPath = options.LogPath,
            Summary = summary,
        };
    }

    /// <summary>
    /// Creates the venv and installs dependencies when needed. Returns the resulting status;
    /// a failure is reported, not thrown, so the desktop can fall back to a system interpreter.
    /// </summary>
    public async Task<PythonRuntimeStatus> ProvisionAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsManagedAndCurrent)
        {
            progress?.Report("Python 运行时已就绪。");
            return status;
        }

        if (!status.UvAvailable)
        {
            progress?.Report(status.CanRun
                ? "未检测到 uv，改用系统 Python（不会创建独立环境）。安装 uv 可获得隔离环境：https://docs.astral.sh/uv/"
                : "未检测到 uv，也没有可用的系统 Python。请安装 Python 3.12+ 或 uv。");
            return status;
        }

        if (!status.VenvExists)
        {
            progress?.Report($"正在创建独立 Python 环境（{options.PythonVersion}）…");
            var created = await RunAsync(
                new ProcessCommand(
                    options.UvExecutable,
                    $"venv \"{options.ResolvedVenvDirectory}\" --python {options.PythonVersion}",
                    options.StateRoot),
                cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                progress?.Report($"创建环境失败，改用系统 Python。详见 {options.LogPath}");
                return await InspectAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report("正在安装分析核心与行情依赖（首次可能需要几分钟）…");
        var target = string.IsNullOrWhiteSpace(options.Extras) ? "." : $".[{options.Extras}]";
        var installed = await RunAsync(
            new ProcessCommand(
                options.UvExecutable,
                $"pip install --python \"{options.VenvInterpreter}\" -e \"{target}\"",
                options.PythonProjectDirectory),
            cancellationToken).ConfigureAwait(false);

        if (!installed.Succeeded)
        {
            progress?.Report($"依赖安装失败。详见 {options.LogPath}");
            return await InspectAsync(cancellationToken).ConfigureAwait(false);
        }

        // The marker is written only after a successful install, so a half-finished run is
        // detected as stale next time instead of being trusted.
        if (status.ManifestHash is not null)
        {
            Directory.CreateDirectory(options.ResolvedVenvDirectory);
            await File.WriteAllTextAsync(options.MarkerPath, status.ManifestHash, cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report("Python 运行时准备完成。");
        return await InspectAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildSummary(bool uv, bool venv, bool current, string? interpreter)
    {
        if (venv && current)
        {
            return "独立环境已就绪，依赖与清单一致。";
        }

        if (venv)
        {
            return "独立环境存在，但依赖与 pyproject.toml 不一致，需要重新安装。";
        }

        if (interpreter is not null)
        {
            return uv
                ? "尚未创建独立环境；当前使用系统 Python。"
                : "未安装 uv，当前使用系统 Python。";
        }

        return "未找到可用的 Python。请安装 Python 3.12+ 或 uv。";
    }

    private async Task<bool> ProbeAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner
                .RunAsync(new ProcessCommand(fileName, arguments, options.StateRoot), cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing executable throws rather than returning non-zero.
            return false;
        }
    }

    private async Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync($"{command.FileName} {command.Arguments} threw: {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return new ProcessResult(-1, "", ex.Message);
        }

        await LogAsync(
            Security.SensitiveValueRedactor.RedactText(
                $"{command.FileName} {command.Arguments} exit={result.ExitCode}\n{result.StandardOutput}\n{result.StandardError}"),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task LogAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogPath) ?? ".");
            await File.AppendAllTextAsync(
                options.LogPath,
                $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Diagnostics must never take the app down.
        }
    }
}
