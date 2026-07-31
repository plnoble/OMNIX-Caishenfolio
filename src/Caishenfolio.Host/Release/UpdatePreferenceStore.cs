using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caishenfolio.Host.Release;

/// <summary>What the user has already decided about updates.</summary>
public sealed class UpdatePreferences
{
    /// <summary>Version the user asked not to be reminded about again.</summary>
    [JsonPropertyName("ignoredVersion")]
    public string IgnoredVersion { get; set; } = "";

    [JsonPropertyName("lastCheckedUtc")]
    public string LastCheckedUtc { get; set; } = "";

    /// <summary>
    /// Whether a silent startup check is worth interrupting the user for.
    ///
    /// Only a genuinely newer release qualifies: a failed check, an up-to-date build, a local
    /// build ahead of the feed, and a version the user already dismissed all stay quiet.
    /// </summary>
    public bool ShouldNotify(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasUpdate || string.IsNullOrEmpty(result.LatestVersion))
        {
            return false;
        }

        return !string.Equals(result.LatestVersion, IgnoredVersion, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Persists update preferences as JSON under the Host State root, next to the other
/// small local-state files. Deliberately separate from the portfolio ledger: dismissing a
/// version release has nothing to do with your holdings.
/// </summary>
public sealed class UpdatePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _gate = new();

    public UpdatePreferenceStore(string stateRootDirectory, string fileName = "update.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootDirectory);
        var root = Path.GetFullPath(stateRootDirectory);
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, fileName);
    }

    public string FilePath => _filePath;

    public UpdatePreferences Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return new UpdatePreferences();
            }

            try
            {
                return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(_filePath))
                       ?? new UpdatePreferences();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A corrupt preference file must not stop the app from starting.
                return new UpdatePreferences();
            }
        }
    }

    public void Save(UpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            try
            {
                File.WriteAllText(_filePath, JsonSerializer.Serialize(preferences, JsonOptions));
            }
            catch (IOException)
            {
                // Losing a preference is not worth taking the app down for.
            }
        }
    }

    /// <summary>Stops the banner reappearing for this version; a newer one still notifies.</summary>
    public UpdatePreferences IgnoreVersion(string version)
    {
        var preferences = Load();
        preferences.IgnoredVersion = (version ?? "").Trim();
        Save(preferences);
        return preferences;
    }

    public UpdatePreferences RecordCheck(DateTimeOffset when)
    {
        var preferences = Load();
        preferences.LastCheckedUtc = when.ToUniversalTime().ToString("O");
        Save(preferences);
        return preferences;
    }
}
