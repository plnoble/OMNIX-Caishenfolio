using System.Text.Json;
using System.Text.Json.Serialization;
using Caishenfolio.Host.Portfolio;

namespace Caishenfolio.Host.Notifications;

/// <summary>What we remember about one alert key between runs.</summary>
public sealed record AlertNotificationRecord
{
    /// <summary>When the alert was last actually delivered (not merely observed).</summary>
    public DateTimeOffset LastNotifiedAt { get; init; }

    /// <summary>When the alert's condition was last seen firing, delivered or not.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>
    /// True when the condition was still firing on the previous check. A condition that
    /// stops and then starts again has *re-entered* — that is a new event, not a repeat.
    /// </summary>
    public bool WasInRange { get; init; }
}

/// <summary>
/// The whole dedup state: one record per alert key. Persisted as JSON next to the other
/// app state; carries no secrets (keys are kind/symbol/title, timestamps only).
/// </summary>
public sealed record AlertNotificationState
{
    public IReadOnlyDictionary<string, AlertNotificationRecord> Records { get; init; } =
        new Dictionary<string, AlertNotificationRecord>(StringComparer.Ordinal);

    public static AlertNotificationState Empty { get; } = new();

    /// <summary>Drops records nobody has seen for a month, so the file cannot grow forever.</summary>
    public AlertNotificationState Pruned(DateTimeOffset now, int keepDays = 30) =>
        new()
        {
            Records = Records
                .Where(pair => now - pair.Value.LastSeenAt < TimeSpan.FromDays(keepDays))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
}

/// <summary>
/// Loads and saves <see cref="AlertNotificationState"/>. A corrupt or missing file yields a
/// fresh state — losing dedup history costs one repeated notification, never a missed one.
/// </summary>
public sealed class AlertNotificationStateStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; } = filePath;

    public AlertNotificationState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return AlertNotificationState.Empty;
            }

            var loaded = JsonSerializer.Deserialize<AlertNotificationState>(
                File.ReadAllText(FilePath), JsonOptions);
            return loaded ?? AlertNotificationState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return AlertNotificationState.Empty;
        }
    }

    public void Save(AlertNotificationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to persist dedup state must never fail the notification run itself.
        }
    }
}

/// <summary>What the gatekeeper decided about one batch of alerts.</summary>
public sealed record GatekeeperDecision
{
    /// <summary>Alerts worth sending now: new, re-entered, or past their cooldown.</summary>
    public required IReadOnlyList<PortfolioAlert> ToSend { get; init; }

    /// <summary>Alerts currently firing that were already delivered within the cooldown.</summary>
    public required IReadOnlyList<PortfolioAlert> Suppressed { get; init; }

    /// <summary>The state to carry into the next run (observation side fully updated).</summary>
    public required AlertNotificationState NextState { get; init; }
}

/// <summary>
/// Decides which firing alerts actually go out this run — the dedup/cooldown/re-entry rules
/// of plan §8, as a pure function so every edge is unit-testable.
///
/// One rule covers both kinds of alert:
/// - send when the key is new, when the condition re-entered (was gone, is back), or when
///   the cooldown since the last *delivery* has expired;
/// - suppress while the condition keeps firing and the last delivery is still fresh.
///
/// Delivery bookkeeping is deliberately separate: <see cref="MarkDelivered"/> advances
/// LastNotifiedAt only for alerts that actually left the machine, so a failed dispatch
/// retries on the next run instead of being swallowed by its own cooldown.
/// </summary>
public static class AlertGatekeeper
{
    /// <summary>
    /// The stable identity of an event: kind + symbol + title, plus the rule identity when the
    /// alert came from a rule (WP-03 R4/D5).
    /// </summary>
    /// <remarks>
    /// The message is excluded on purpose: it embeds the live price, so keying on it would
    /// make every run a "new" alert. Changing a planned level's value keeps the old key —
    /// the exit/re-entry rule still re-fires when the condition genuinely resets.
    /// Alerts that are not rule-driven (IPO deadlines, price levels) keep exactly the old key,
    /// so their dedupe behaviour is untouched (R7).
    /// </remarks>
    public static string KeyOf(PortfolioAlert alert) =>
        string.IsNullOrEmpty(alert.RuleId)
            ? $"{alert.Kind}|{alert.Symbol}|{alert.Title}"
            : $"{alert.Kind}|{alert.Symbol}|{alert.Title}|rule:{alert.RuleId}@r{alert.RuleRevision}#{alert.RuleLevel}";

    /// <summary>
    /// The pre-WP-03 record for a key that now carries a rule identity: the legacy key is exactly
    /// the new key's prefix (kind|symbol|title). Conservative on purpose (R5/D6) — adopting it can
    /// at worst delay one repeat, while ignoring it would re-send everything on upgrade day.
    /// </summary>
    private static string LegacyPrefixOf(string key)
    {
        var marker = key.IndexOf("|rule:", StringComparison.Ordinal);
        return marker < 0 ? key : key[..marker];
    }

    private static AlertNotificationRecord? AdoptLegacy(
        IReadOnlyDictionary<string, AlertNotificationRecord> records,
        string key,
        IReadOnlyList<string> legacyLevels)
    {
        var legacyKey = LegacyPrefixOf(key);
        if (legacyKey == key)
        {
            return null;
        }

        // Pre-WP-03: the record was filed under the bare kind|symbol|title key.
        if (records.TryGetValue(legacyKey, out var bare))
        {
            return bare;
        }

        // WP-03 before the level format was fixed: same prefix, same identity, same revision, but a
        // level string the rule no longer produces (e.g. "band:7.1~-" for a threshold that now keeps
        // "band:7.1000001~-"). The level is part of the key, so without this lookup the rule misses
        // the record holding its cooldown and is delivered again inside it (review #2).
        var separator = key.IndexOf('#', key.IndexOf("|rule:", StringComparison.Ordinal));
        if (separator < 0)
        {
            return null;
        }

        var head = key[..(separator + 1)];
        foreach (var level in legacyLevels)
        {
            if (records.TryGetValue(head + level, out var aliased))
            {
                return aliased;
            }
        }

        return null;
    }

    public static GatekeeperDecision Filter(        IReadOnlyList<PortfolioAlert> alerts,
        AlertNotificationState state,
        DateTimeOffset now,
        TimeSpan cooldown,
        IReadOnlySet<string>? notEvaluated = null)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(state);

        var toSend = new List<PortfolioAlert>();
        var suppressed = new List<PortfolioAlert>();
        var records = new Dictionary<string, AlertNotificationRecord>(
            state.Records, StringComparer.Ordinal);

        var firing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alert in alerts)
        {
            var key = KeyOf(alert);
            firing.Add(key);
            // WP-03 R5/D6: a state file written before the key carried a rule identity must not
            // read as "never sent" — that would re-send everything on the upgrade day. The old
            // record is adopted as this key's history; it stays in the file until it ages out.
            var previous = records.GetValueOrDefault(key)
                ?? AdoptLegacy(records, key, alert.LegacyRuleLevels);

            var send = previous is null
                || !previous.WasInRange
                || now - previous.LastNotifiedAt >= cooldown;

            if (send)
            {
                toSend.Add(alert);
                records[key] = new AlertNotificationRecord
                {
                    LastNotifiedAt = previous?.LastNotifiedAt ?? DateTimeOffset.MinValue,
                    LastSeenAt = now,
                    WasInRange = true,
                };
            }
            else
            {
                suppressed.Add(alert);
                records[key] = previous! with { LastSeenAt = now, WasInRange = true };
            }
        }

        // Conditions that are no longer firing have left their range; the next time they
        // fire is a re-entry and must notify immediately.
        //
        // A condition we could not evaluate is not "left the range" (WP-02 R6/A10). Keeping its
        // record untouched means the outage neither advances the cooldown nor fabricates a
        // re-entry when the quote comes back (A11).
        var unknown = notEvaluated ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        // WP-03 D6, review #3: an old record must not be read as "left the range" merely because the
        // same condition is now reported under its new rule identity. While any key that adopts the
        // old one is firing OR un-evaluated, that old prefix is still live. Without this, the first
        // upgrade-day round that cannot be evaluated writes a fabricated exit, and the next round
        // re-sends inside the cooldown the adoption was supposed to respect.
        var livePrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var liveKey in firing.Concat(unknown))
        {
            livePrefixes.Add(LegacyPrefixOf(liveKey));
        }

        foreach (var (key, record) in records)
        {
            if (unknown.Contains(key))
            {
                continue;
            }

            if (!firing.Contains(key) && record.WasInRange && !livePrefixes.Contains(key))
            {
                records[key] = record with { WasInRange = false, LastSeenAt = now };
            }
        }

        return new GatekeeperDecision
        {
            ToSend = toSend,
            Suppressed = suppressed,
            NextState = new AlertNotificationState { Records = records },
        };
    }

    /// <summary>
    /// Records that these alerts actually left the machine, so their cooldown starts now.
    /// Alerts that were observed but not delivered keep their old timestamp — the next run
    /// will still consider them due.
    /// </summary>
    public static AlertNotificationState MarkDelivered(
        AlertNotificationState state,
        IReadOnlyList<PortfolioAlert> delivered,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(delivered);

        var records = new Dictionary<string, AlertNotificationRecord>(
            state.Records, StringComparer.Ordinal);
        foreach (var alert in delivered)
        {
            var key = KeyOf(alert);
            var seen = records.GetValueOrDefault(key);
            records[key] = new AlertNotificationRecord
            {
                LastNotifiedAt = now,
                LastSeenAt = seen?.LastSeenAt ?? now,
                WasInRange = seen?.WasInRange ?? true,
            };
        }

        return new AlertNotificationState { Records = records };
    }
}
