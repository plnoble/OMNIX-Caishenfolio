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
    /// Every key this alert may currently be recorded under: its own, plus the complete keys an
    /// earlier build wrote for the same rule (<see cref="PortfolioAlert.LegacyRuleKeys"/>). Callers
    /// use the whole set when deciding whether a condition is still live — an upgrade must not
    /// fabricate an exit for a key the rule merely stopped producing (WP-03 review #C4-4).
    /// </summary>
    public static IReadOnlyList<string> KeysOf(PortfolioAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var keys = new List<string> { KeyOf(alert) };
        foreach (var legacy in alert.LegacyRuleKeys)
        {
            keys.Add($"{alert.Kind}|{alert.Symbol}|{alert.Title}|rule:{legacy}");
        }

        return keys;
    }

    /// <summary>
    /// The pre-rule part of a key: <c>kind|symbol|title</c>. Records written before keys carried a
    /// rule identity were filed under exactly this, and every rule with the same wording shares it.
    /// </summary>
    private static string BarePrefixOf(string key)
    {
        var marker = key.IndexOf("|rule:", StringComparison.Ordinal);
        return marker < 0 ? key : key[..marker];
    }

    /// <summary>
    /// The record this key can be matched to: the complete keys an earlier build wrote for the same
    /// rule, then the bare pre-WP-03 <c>kind|symbol|title</c> key. Conservative on purpose (R5/D6) —
    /// adopting a record can at worst delay one repeat, while ignoring it would re-send on upgrade
    /// day.
    /// </summary>
    /// <remarks>
    /// Candidate keys are COMPOSED from structured fields (this alert's own kind/symbol/title plus
    /// the rule's own recorded identity/revision/level), never parsed out of the current key, so an
    /// unusual identity string cannot truncate the lookup and pull in another rule's record (WP-03
    /// review #C4-6). Among the candidates the most recent delivery fact wins: a low-precision bare
    /// record may not override the rule's own newer complete-key record (#C4-5).
    /// </remarks>
    private static AlertNotificationRecord? AdoptLegacy(
        IReadOnlyDictionary<string, AlertNotificationRecord> records,
        string key,
        IReadOnlyList<string> legacyRuleKeys)
    {
        var marker = key.IndexOf("|rule:", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var prefix = key[..marker];
        var candidates = new List<AlertNotificationRecord>();
        foreach (var legacy in legacyRuleKeys)
        {
            if (records.TryGetValue($"{prefix}|rule:{legacy}", out var aliased))
            {
                candidates.Add(aliased);
            }
        }

        if (records.TryGetValue(prefix, out var bare))
        {
            candidates.Add(bare);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var newest = candidates[0];
        foreach (var candidate in candidates)
        {
            if (candidate.LastNotifiedAt > newest.LastNotifiedAt)
            {
                newest = candidate;
            }
        }

        return newest;
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

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alert in alerts)
        {
            var key = KeyOf(alert);
            // Everything this alert may be recorded under is live: its own key, plus the complete
            // keys an earlier build wrote for it.
            live.UnionWith(KeysOf(alert));
            // WP-03 R5/D6: a state file written before the key carried a rule identity must not
            // read as "never sent" — that would re-send everything on the upgrade day. The old
            // record is adopted as this key's history; it stays in the file until it ages out.
            var previous = records.GetValueOrDefault(key)
                ?? AdoptLegacy(records, key, alert.LegacyRuleKeys);

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
        live.UnionWith(unknown);

        // A record may also be filed under the bare pre-WP-03 key, which is shared by everything with
        // the same kind/symbol/title. As long as ANY key with that prefix is live, that record is not
        // an exit — it is the shared history the adopting rule is still using.
        foreach (var liveKey in live.ToList())
        {
            live.Add(BarePrefixOf(liveKey));
        }

        // WP-03 D6, review #3 and #C4-4: a record must not be read as "left the range" merely because
        // the same condition is now reported under a key the upgrade produced. Every key the rule can
        // be recorded under — its own, the complete keys an earlier build wrote, and any key an
        // un-evaluated round names — stays live for this round. Otherwise the first upgrade-day round
        // that cannot be evaluated (or that fires under the new key) writes a fabricated exit, and the
        // next round re-sends inside the cooldown the adoption was supposed to respect.
        foreach (var (key, record) in records)
        {
            if (!live.Contains(key) && record.WasInRange)
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
