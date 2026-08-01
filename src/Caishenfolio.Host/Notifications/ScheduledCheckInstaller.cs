using System.Diagnostics;
using System.Globalization;

namespace Caishenfolio.Host.Notifications;

/// <summary>Outcome of a schtasks call, with the command shown so it can be run by hand instead.</summary>
public sealed record ScheduleResult
{
    public required bool Ok { get; init; }
    public required string Command { get; init; }
    public string Output { get; init; } = "";

    public static ScheduleResult Failure(string command, string output) =>
        new() { Ok = false, Command = command, Output = output };
}

/// <summary>
/// Registers the daily background check as a Windows scheduled task.
///
/// This runs only when the user asks for it from the settings window. Creating a scheduled task
/// is a change to how the machine behaves when the app is not running, so it is not something to
/// switch on quietly on the user's behalf — and the exact command is surfaced either way, so the
/// user can inspect it, run it themselves, or remove the task later without this code.
/// </summary>
public static class ScheduledCheckInstaller
{
    public const string TaskName = "OMNIX-Caishenfolio 打新提醒";

    /// <summary>The command that would be run, for display before anything is changed.</summary>
    public static string DescribeInstall(string executablePath, TimeOnly runAt) =>
        BuildArguments(executablePath, runAt) is var args
            ? "schtasks " + string.Join(" ", args)
            : "";

    public static ScheduleResult Install(string executablePath, TimeOnly runAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return Run(BuildArguments(executablePath, runAt));
    }

    public static ScheduleResult Uninstall() =>
        Run(["/Delete", "/TN", TaskName, "/F"]);

    /// <summary>True when a task by this name already exists.</summary>
    public static bool IsInstalled() => Run(["/Query", "/TN", TaskName]).Ok;

    private static string[] BuildArguments(string executablePath, TimeOnly runAt) =>
    [
        "/Create",
        "/SC", "DAILY",
        "/TN", TaskName,
        // The quotes stay inside the value: schtasks passes /TR through to the shell verbatim,
        // and an install path with a space would otherwise become two arguments.
        "/TR", $"\"{executablePath}\" --notify",
        "/ST", runAt.ToString("HH:mm", CultureInfo.InvariantCulture),
        "/F",
    ];

    private static ScheduleResult Run(string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var shown = "schtasks " + string.Join(" ", arguments);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ScheduleResult.Failure(shown, "无法启动 schtasks。");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);

            return new ScheduleResult
            {
                Ok = process.HasExited && process.ExitCode == 0,
                Command = shown,
                Output = output.Trim(),
            };
        }
        catch (Exception ex)
        {
            return ScheduleResult.Failure(shown, ex.Message);
        }
    }
}
