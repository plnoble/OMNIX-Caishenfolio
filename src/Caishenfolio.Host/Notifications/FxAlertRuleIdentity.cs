namespace Caishenfolio.Host.Notifications;

/// <summary>
/// WP-03 D5, second pass (audit): a rule's identity must be **created and persisted**, not derived
/// at comparison time. Deriving it from the kind and the currencies produced a *classification*,
/// so two rules the user created separately — same pair, same threshold, different fixed fee —
/// shared one dedupe key and silenced each other.
///
/// The three places identity is settled, all here so they cannot drift apart:
/// <list type="bullet">
/// <item>creating a rule (the settings page) — <see cref="AssignNewIdentity"/>;</item>
/// <item>editing a rule — <see cref="WithEditedParameters"/>, which keeps the id and advances the
/// revision only when what the rule watches for actually changed;</item>
/// <item>saving — <see cref="Reconcile"/>, which backfills ids for rules written by an older
/// version and advances the revision if the parameters changed outside the editor.</item>
/// </list>
/// </summary>
public static class FxAlertRuleIdentity
{
    /// <summary>
    /// A fresh durable identity. Random rather than content-derived on purpose: two rules with the
    /// same parameters are still two watches the user created, and a content hash would fuse them
    /// again. It is also why changing a rule keeps its id — only <c>Revision</c> moves.
    /// </summary>
    public static string NewId() => "rule-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Gives a rule its identity if it has none yet; otherwise returns it unchanged.</summary>
    public static FxAlertRule AssignNewIdentity(FxAlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrWhiteSpace(rule.Id) ? rule with { Id = NewId() } : rule;
    }

    /// <summary>
    /// Applies an edit to an existing rule: the identity is the existing rule's, and the revision
    /// advances only when <see cref="WatchChanged"/> says the watch itself changed. Toggling
    /// <c>Enabled</c> or rewording a note is not a new watch, so it does not reset the cooldown.
    /// </summary>
    public static FxAlertRule WithEditedParameters(FxAlertRule existing, FxAlertRule edited)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(edited);

        var settled = AssignNewIdentity(existing) with
        {
            Kind = edited.Kind,
            Pair = edited.Pair,
            Lower = edited.Lower,
            Upper = edited.Upper,
            BaseCurrency = edited.BaseCurrency,
            TargetCurrency = edited.TargetCurrency,
            HeldCurrency = edited.HeldCurrency,
            ThresholdPct = edited.ThresholdPct,
            ThresholdAmount = edited.ThresholdAmount,
            SpreadPct = edited.SpreadPct,
            FixedFee = edited.FixedFee,
            Enabled = edited.Enabled,
        };

        return WatchChanged(existing, settled)
            ? settled with { Revision = existing.Revision + 1 }
            : settled with { Revision = existing.Revision };
    }

    /// <summary>
    /// The persistence boundary: every rule that reaches storage leaves with an id, and a rule whose
    /// parameters changed behind the editor's back gets its revision advanced exactly once. Called
    /// by <see cref="NotificationSettingsStore.Save"/>, so no caller can write rules without
    /// identity — that is the whole point of the audit finding.
    /// </summary>
    public static IReadOnlyList<FxAlertRule> Reconcile(
        IReadOnlyList<FxAlertRule> incoming,
        IReadOnlyList<FxAlertRule> stored)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(stored);

        var previousById = new Dictionary<string, FxAlertRule>(StringComparer.Ordinal);
        foreach (var rule in stored)
        {
            if (!string.IsNullOrWhiteSpace(rule.Id))
            {
                previousById[rule.Id.Trim()] = rule;
            }
        }

        var settled = new List<FxAlertRule>(incoming.Count);
        foreach (var rule in incoming)
        {
            var assigned = AssignNewIdentity(rule);
            if (previousById.TryGetValue(assigned.Id.Trim(), out var previous)
                && WatchChanged(previous, assigned)
                && assigned.Revision <= previous.Revision)
            {
                // The editor did not account for this change (it arrived with an equal or older
                // revision), so this is where the revision advances. If it already did, its larger
                // revision stands — never bump twice for one edit.
                assigned = assigned with { Revision = previous.Revision + 1 };
            }

            settled.Add(assigned);
        }

        return settled;
    }

    /// <summary>
    /// True when the two rules watch for different things. Deliberately blind to <c>Id</c>,
    /// <c>Revision</c> and <c>Enabled</c>: those are identity and bookkeeping, not the watch.
    /// </summary>
    public static bool WatchChanged(FxAlertRule a, FxAlertRule b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return a.Kind != b.Kind
            || FxAlertRule.Normalize(a.Pair) != FxAlertRule.Normalize(b.Pair)
            || a.Lower != b.Lower
            || a.Upper != b.Upper
            || FxAlertRule.Normalize(a.BaseCurrency) != FxAlertRule.Normalize(b.BaseCurrency)
            || FxAlertRule.Normalize(a.TargetCurrency) != FxAlertRule.Normalize(b.TargetCurrency)
            || FxAlertRule.Normalize(a.HeldCurrency) != FxAlertRule.Normalize(b.HeldCurrency)
            || a.ThresholdPct != b.ThresholdPct
            || a.ThresholdAmount != b.ThresholdAmount
            || a.SpreadPct != b.SpreadPct
            || a.FixedFee != b.FixedFee;
    }
}