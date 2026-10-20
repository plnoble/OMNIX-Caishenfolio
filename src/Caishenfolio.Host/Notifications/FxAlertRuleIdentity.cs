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
    /// upgrading does not change its notification key. Whether it keeps that identity is decided per
    /// (identity, level) in <see cref="Reconcile"/>: two legacy rules that differ only by their
    /// threshold were two independent watches, and both keep their own key.
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
        var disambiguated = new Dictionary<string, Queue<FxAlertRule>>(StringComparer.Ordinal);
        foreach (var rule in stored)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                continue;
            }

            previousById[rule.Id.Trim()] = rule;

            // A rule that owns an identity of its own (not the inherited one) is what a previous
            // round handed to one member of a colliding group. Remember it so a retry of the SAME
            // identity-less batch lands on the same identity instead of inventing another one.
            if (!string.Equals(rule.Id.Trim(), rule.DerivedId, StringComparison.Ordinal))
            {
                var signature = Signature(rule);
                if (!disambiguated.TryGetValue(signature, out var queue))
                {
                    disambiguated[signature] = queue = new Queue<FxAlertRule>();
                }

                queue.Enqueue(rule);
            }
        }

        var settled = new List<FxAlertRule>(incoming.Count);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in incoming)
        {
            var assigned = string.IsNullOrWhiteSpace(rule.Id)
                ? AdoptForMigration(rule, claimed, disambiguated)
                : rule;

            assigned = SettleLegacyLevels(assigned, previousById);
            assigned = SettleRevision(assigned, previousById);
            settled.Add(assigned);
        }

        return settled;
    }

    /// <summary>
    /// Migration for a rule that carries no identity. The inherited identity is claimed per
    /// <b>(identity, level)</b> — not per identity — because the complete key contains both: two
    /// legacy rules that differ only by their threshold were two independent watches, and giving the
    /// second one a fresh identity would throw away the key its cooldown lives under (audited
    /// regression). Only a genuine duplicate of the same identity AND level takes a fresh identity,
    /// and even then a previously assigned one is reused so retrying a save is idempotent.
    /// </summary>
    private static FxAlertRule AdoptForMigration(
        FxAlertRule rule,
        HashSet<string> claimed,
        Dictionary<string, Queue<FxAlertRule>> disambiguated)
    {
        var inherited = AdoptInheritedIdentity(rule);
        var signature = Signature(inherited);
        if (claimed.Add(signature))
        {
            return inherited;
        }

        if (disambiguated.TryGetValue(signature, out var queue) && queue.Count > 0)
        {
            var previous = queue.Dequeue();
            return inherited with { Id = previous.Id.Trim(), Revision = previous.Revision };
        }

        // A genuine duplicate: nothing in the state file can tell these two apart, and under the old
        // fallback they suppressed each other. It gets an identity of its own — from the id-less
        // incoming rule, because the inherited one already carries the shared identity.
        return AssignNewIdentity(rule);
    }

    /// <summary>
    /// Keeps every level string this rule may already have been recorded under, so the upgrade can
    /// find its old key even after the level format changed (a threshold with more than six decimals
    /// went from "band:7.1~-" to "band:7.1000001~-"). Merges what a stored copy already knew.
    /// </summary>
    private static FxAlertRule SettleLegacyLevels(
        FxAlertRule rule,
        Dictionary<string, FxAlertRule> previousById)
    {
        var levels = new List<string>(rule.LegacyLevels);
        if (!string.Equals(rule.LegacyLevel, rule.Level, StringComparison.Ordinal)
            && !levels.Contains(rule.LegacyLevel, StringComparer.Ordinal))
        {
            levels.Add(rule.LegacyLevel);
        }

        if (previousById.TryGetValue(rule.Id.Trim(), out var previous))
        {
            foreach (var level in previous.LegacyLevels)
            {
                if (!levels.Contains(level, StringComparer.Ordinal))
                {
                    levels.Add(level);
                }
            }
        }

        return levels.Count == rule.LegacyLevels.Count ? rule : rule with { LegacyLevels = levels };
    }

    /// <summary>
    /// One revision bump per edit, and never backwards: a caller that hands back a stale copy must
    /// not roll the stored revision down, because the revision is part of the key.
    /// </summary>
    private static FxAlertRule SettleRevision(
        FxAlertRule assigned,
        Dictionary<string, FxAlertRule> previousById)
    {
        if (!previousById.TryGetValue(assigned.Id.Trim(), out var previous))
        {
            return assigned;
        }

        if (WatchChanged(previous, assigned) && assigned.Revision <= previous.Revision)
        {
            return assigned with { Revision = previous.Revision + 1 };
        }

        return assigned.Revision < previous.Revision
            ? assigned with { Revision = previous.Revision }
            : assigned;
    }

    /// <summary>
    /// What makes two rules the same watch for migration purposes: the identity they inherit plus
    /// the level that is part of their notification key.
    /// </summary>
    private static string Signature(FxAlertRule rule) => $"{rule.DerivedId}\u0001{rule.Level}";

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