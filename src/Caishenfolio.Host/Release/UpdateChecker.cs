using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Caishenfolio.Host.Release;

public enum UpdateStatus
{
    /// <summary>Running the newest published release.</summary>
    UpToDate,
    /// <summary>A newer release exists.</summary>
    UpdateAvailable,
    /// <summary>Running a build ahead of anything published (a local dev build).</summary>
    Ahead,
    /// <summary>The check itself did not complete; the app is unaffected.</summary>
    Failed,
}

public sealed record UpdateCheckResult
{
    public required UpdateStatus Status { get; init; }
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? Notes { get; init; }
    public required string Message { get; init; }

    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>The published release the app compares itself against.</summary>
public sealed record ReleaseInfo(string TagName, string HtmlUrl, string? Body);

/// <summary>Source of published releases, so the comparison can be tested without network.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> TryGetLatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the latest release from the GitHub REST API. Read-only, unauthenticated.</summary>
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    private readonly HttpClient _http;
    private readonly string _repository;

    public GitHubReleaseFeed(HttpClient http, string repository = "plnoble/OMNIX-Caishenfolio")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _repository = repository;
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd($"{ProductInfo.Brand}-Caishenfolio"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("OMNIX-Caishenfolio");
        }
    }

    public async Task<ReleaseInfo?> TryGetLatestAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_repository}/releases/latest";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A repo with no releases yet answers 404; that is "nothing published", not an error.
            return null;
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GitHubRelease>(cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(payload?.TagName)
            ? null
            : new ReleaseInfo(payload!.TagName, payload.HtmlUrl ?? "", payload.Body);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}

/// <summary>
/// Compares the running build against the newest published release.
///
/// The check is advisory and fail-closed: a network problem reports "检查失败" and the app keeps
/// working. Nothing is downloaded or installed automatically — upgrading is the user's action.
/// </summary>
public sealed class UpdateChecker(IReleaseFeed feed)
{
    public async Task<UpdateCheckResult> CheckAsync(
        string? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        var current = currentVersion ?? ProductInfo.Version;

        ReleaseInfo? latest;
        try
        {
            latest = await feed.TryGetLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Failed,
                CurrentVersion = current,
                Message = $"检查更新失败：{ex.Message}（不影响使用）",
            };
        }

        if (latest is null)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                CurrentVersion = current,
                Message = $"当前 v{current}；仓库还没有发布过 Release。",
            };
        }

        if (!TryParse(latest.TagName, out var latestVersion) || !TryParse(current, out var currentParsed))
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Failed,
                CurrentVersion = current,
                LatestVersion = latest.TagName,
                ReleaseUrl = latest.HtmlUrl,
                Message = $"无法比较版本号：本地 '{current}'，远端 '{latest.TagName}'。",
            };
        }

        var comparison = currentParsed.CompareTo(latestVersion);
        var latestText = Format(latestVersion);

        return comparison switch
        {
            < 0 => new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable,
                CurrentVersion = current,
                LatestVersion = latestText,
                ReleaseUrl = latest.HtmlUrl,
                Notes = latest.Body,
                Message = $"有新版本 v{latestText}（当前 v{current}）。下载后运行 MSI 即可原地升级，账本数据不受影响。",
            },
            > 0 => new UpdateCheckResult
            {
                Status = UpdateStatus.Ahead,
                CurrentVersion = current,
                LatestVersion = latestText,
                ReleaseUrl = latest.HtmlUrl,
                Message = $"当前 v{current} 比已发布的 v{latestText} 还新（本地开发版）。",
            },
            _ => new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                CurrentVersion = current,
                LatestVersion = latestText,
                ReleaseUrl = latest.HtmlUrl,
                Message = $"已是最新版本 v{current}。",
            },
        };
    }

    /// <summary>Parses <c>1.2.3</c> or <c>v1.2.3</c>; extra parts and suffixes are rejected.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var parts = trimmed.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    private static string Format(Version version) =>
        $"{version.Major}.{version.Minor}.{version.Build}";
}
