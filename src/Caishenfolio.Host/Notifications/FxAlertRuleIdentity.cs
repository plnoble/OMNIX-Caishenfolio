namespace Caishenfolio.Host.Notifications;

/// <summary>
/// WP-03 D5: a rule's identity must be **created and persisted**, not derived at comparison time.
/// Deriving it from the kind and the currencies produced a *classification*, so two rules the user
/// created separately — same pair, same threshold, different fixed fee — shared one dedupe key and
/// silenced each other.
///
/// Two different jobs live here, and mixing them up is how the upgrade breaks:
/// <list type="bullet">
/// <item><b>Creation</b> (<see cref="AssignNewIdentity"/>) gives a rule a fresh random id. Random
/// on purpose: two rules with identical parameters are still two watches the user created.</item>
/// <item><b>Migration</b> (<see cref="AdoptInheritedIdentity"/>) gives a rule written before ids
/// existed the identity its notification keys were ALREADY built from. Handing it a fresh random id
/// instead would change its key on upgrade day and re-send an alert the user already saw (A9/D6).</item>
/// <item><b>Editing</b> (<see cref="WithEditedParameters"/>) keeps the id and advances the revision
/// only when what the rule watches for actually changed.</item>
/// <item><b>Persistence</b> (<see cref="Reconcile"/>) settles identity and revision on every write,
/// and is where a change made behind the editor's back still moves the revision — exactly once.</item>
/// </list>
/// </summary>
public static class FxAlertRuleIdentity
{
    /// <summary>
    /// A fresh durable identity for a rule the user is creating now. Random rather than
    /// content-derived: a content hash would fuse two rules that happen to watch the same thing.
    /// </summary>
    public static string NewId() => "rule-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Gives a newly created rule its own identity; a rule that already has one is returned unchanged.</summary>
    public static FxAlertRule AssignNewIdentity(FxAlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrWhiteSpace(rule.Id) ? rule with { Id = NewId() } : rule;
    }

    /// <summary>
    /// Migration: keeps the identity the rule was already deduped by (its derived identity), so
    /// upgrading does not change a single notification key. Two legacy rules that were identical in
    /// every watched field were already sharing one key, and the state file cannot tell them apart —
    /// the first keeps that key (its history stays attached), and <see cref="Reconcile"/> gives the
    /// duplicate an identity of its own instead of leaving it suppressed forever.
    /// </summary>
    public static FxAlertRule AdoptInheritedIdentity(FxAlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrWhiteSpace(rule.Id) ? rule with { Id = rule.DerivedId } : rule;
    }

    /// <summary>
    /// Applies an edit to an existing rule: the identity is the existing rule's, and the revision
    /// advances only when <see cref="WatchChanged"/> says the watch itself changed. The
    /// <c>Enabled</c> flag is the existing rule's too — editing the parameters of a rule the user
    /// switched off must not switch it back on.
    /// </summary>
    public static FxAlertRule WithEditedParameters(FxAlertRule existing, FxAlertRule edited)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(edited);

        var settled = AdoptInheritedIdentity(existing) with
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
        };

        return WatchChanged(existing, settled)
            ? settled with { Revision = existing.Revision + 1 }
            : settled;
    }

    /// <summary>
    /// The persistence boundary: every rule that reaches storage leaves with an identity, a revision
    /// that never moves backwards, and a revision that advances when the parameters changed behind
    /// the editor's back — exactly once. Called by <see cref="NotificationSettingsStore"/>, so no
    /// caller can write rules without identity; the whole point of the audit finding.
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
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in incoming)
        {
            var assigned = AdoptInheritedIdentity(rule);
            if (string.IsNullOrWhiteSpace(rule.Id) && taken.Contains(assigned.Id.Trim()))
            {
                // A second rule that used to share this inherited identity: under the old fallback
                // the two suppressed each other, so it gets an id of its own (which at worst
                // delivers the alert it was being denied). Only the first one keeps the inherited
                // key, so the notification history that existed stays attached to a rule.
                assigned = AssignNewIdentity(rule);
            }

            taken.Add(assigned.Id.Trim());

            if (previousById.TryGetValue(assigned.Id.Trim(), out var previous))
            {
                if (WatchChanged(previous, assigned) && assigned.Revision <= previous.Revision)
                {
                    // The editor did not account for this change (it arrived with an equal or older
                    // revision), so this is where the revision advances. If it already did, its
                    // larger revision stands — never bump twice for one edit.
                    assigned = assigned with { Revision = previous.Revision + 1 };
                }
                else if (assigned.Revision < previous.Revision)
                {
                    // A caller that hands back a stale copy must not roll the stored revision back:
                    // the revision is part of the notification key, so going backwards would look
                    // like a new watch and re-send.
                    assigned = assigned with { Revision = previous.Revision };
                }
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