using System.Globalization;

namespace Caishenfolio.Host.Notifications;

/// <summary>What the settings page can honestly say about the last background run.</summary>
public sealed record BackgroundCheckStatus
{
    /// <summary>True when the scheduled task exists at all.</summary>
    public required bool Scheduled { get; init; }
    /// <summary>Last log line of the headless run, or null when there is no log yet.</summary>
    public string? LastRun { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastRunSummary { get; init; }

    /// <summary>
    /// Plain-language capability statement. With extended checks off it must say prices are
    /// NOT checked in the background (plan N0.3's honesty rule); with them on it says what
    /// layer 2 really does — including that it starts the analytics core and can fail
    /// without affecting the deadline layer.
    /// </summary>
    public string Scope =>
        ExtendedChecks
            ? "后台检查覆盖：打新时限（本地，始终执行）+ 价格/集中度/数据质量（需启动分析核心，较慢；失败不影响打新提醒，失败原因会记录在下方）。"
            : "后台检查只覆盖打新时限（中签缴款、即将上市）。价格、集中度、数据质量提醒需要打开软件，或在下方开启「后台扩展检查」。";

    /// <summary>Whether extended (layer-2) checks are switched on.</summary>
    public bool ExtendedChecks { get; init; }
}

/// <summary>
/// Reads the notify log the headless runner writes, so the settings page can show when the
/// background check actually ran and what it found — instead of implying it by the task existing.
///
/// The log holds no secrets by construction (the headless runner writes only counts, channels,
/// and failure reasons), but the tail is taken defensively anyway: a future format change cannot
/// dump an unbounded blob into the settings window.
/// </summary>
public static class NotifyLogReader
{
    public const int MaxLineLength = 400;

    public static BackgroundCheckStatus Read(
        bool scheduled,
        string? logPath = null,
        bool extendedChecks = false)
    {
        var path = logPath ?? HeadlessNotifier.DefaultLogPath();
        if (!File.Exists(path))
        {
            return new BackgroundCheckStatus { Scheduled = scheduled, ExtendedChecks = extendedChecks };
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? last = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length > 0)
                {
                    last = line;
                }
            }

            if (last is null)
            {
                return new BackgroundCheckStatus { Scheduled = scheduled, ExtendedChecks = extendedChecks };
            }

            last = last.Trim();
            var clipped = last.Length > MaxLineLength ? last[..MaxLineLength] + "…" : last;

            return new BackgroundCheckStatus
            {
                Scheduled = scheduled,
                ExtendedChecks = extendedChecks,
                // "2026-09-04 09:00:01  <summary>"
                LastRunAt = DateTimeOffset.TryParseExact(
                    last[..Math.Min(19, last.Length)],
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var at)
                    ? at
                    : null,
                LastRunSummary = last.Length > 21 ? last[21..].Trim() : null,
                LastRun = clipped,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BackgroundCheckStatus { Scheduled = scheduled, ExtendedChecks = extendedChecks };
        }
    }
}
