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

        var storedById = new Dictionary<string, FxAlertRule>(StringComparer.Ordinal);
        var storedBySignature = new Dictionary<string, List<FxAlertRule>>(StringComparer.Ordinal);
        foreach (var rule in stored)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                continue;
            }

            storedById[rule.Id.Trim()] = rule;
            var signature = Signature(rule);
            if (!storedBySignature.TryGetValue(signature, out var matches))
            {
                storedBySignature[signature] = matches = [];
            }

            matches.Add(rule);
        }

        // Identities the batch states explicitly can never be handed to another rule: that is how a
        // partially migrated / partially backfilled input stays consistent (review #C4-2).
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in incoming)
        {
            if (!string.IsNullOrWhiteSpace(rule.Id))
            {
                reserved.Add(rule.Id.Trim());
            }
        }

        // The inherited identity of a rule that already carries it is spoken for, too.
        var claimedDerived = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in incoming)
        {
            if (!string.IsNullOrWhiteSpace(rule.Id)
                && string.Equals(rule.Id.Trim(), rule.DerivedId, StringComparison.Ordinal))
            {
                claimedDerived.Add(rule.DerivedId);
            }
        }

        var cursor = new Dictionary<string, int>(StringComparer.Ordinal);
        var settled = new List<FxAlertRule>(incoming.Count);
        foreach (var rule in incoming)
        {
            var assigned = string.IsNullOrWhiteSpace(rule.Id)
                ? AdoptForMigration(rule, storedBySignature, cursor, reserved, claimedDerived)
                : rule;

            assigned = SettleLegacyKeys(assigned, rule, storedById);
            assigned = SettleRevision(assigned, storedById);
            reserved.Add(assigned.Id.Trim());
            settled.Add(assigned);
        }

        return settled;
    }

    /// <summary>
    /// Migration for a rule that carries no identity. The FIRST match is by what the rule actually
    /// watches (<see cref="Signature"/>): a rule that was already saved under an identity keeps it,
    /// whatever order the settings file lists things in, which is what makes a retry, a reorder or a
    /// removal stable (review #C4-2/#C4-3). Only rules the store cannot tell apart — same watch, same
    /// level — are matched positionally. With no match at all, the rule inherits the identity its
    /// notification keys were already built from, and only a genuine duplicate of that identity gets
    /// one of its own.
    /// </summary>
    private static FxAlertRule AdoptForMigration(
        FxAlertRule rule,
        Dictionary<string, List<FxAlertRule>> storedBySignature,
        Dictionary<string, int> cursor,
        HashSet<string> reserved,
        HashSet<string> claimedDerived)
    {
        var signature = Signature(rule);
        if (storedBySignature.TryGetValue(signature, out var matches))
        {
            var start = cursor.GetValueOrDefault(signature);
            for (var i = start; i < matches.Count; i++)
            {
                if (reserved.Contains(matches[i].Id.Trim()))
                {
                    continue;
                }

                cursor[signature] = i + 1;
                var match = matches[i];
                return rule with
                {
                    Id = match.Id.Trim(),
                    Revision = match.Revision,
                    LegacyRuleKeys = match.LegacyRuleKeys,
                };
            }
        }

        return claimedDerived.Add(rule.DerivedId) ? AdoptInheritedIdentity(rule) : AssignNewIdentity(rule);
    }

    /// <summary>
    /// Keeps every COMPLETE key this rule may already have been recorded under: the earlier build
    /// composed "{identity}@r{revision}#{level}" with the identity it had then and the six-decimal
    /// level. Both the identity it used (its derived one, when it had no id) and the level it used are
    /// remembered, so an upgrade finds the record that holds the cooldown — the level alone is not
    /// enough once a rule had to be given a fresh identity (audited regression). Merges what a stored
    /// copy already knew.
    /// </summary>
    private static FxAlertRule SettleLegacyKeys(
        FxAlertRule rule,
        FxAlertRule incoming,
        Dictionary<string, FxAlertRule> storedById)
    {
        var keys = new List<string>(rule.LegacyRuleKeys);
        var current = $"{rule.StableId}@r{rule.Revision}#{rule.Level}";
        foreach (var legacy in new[]
                 {
                     $"{incoming.DerivedId}@r{incoming.Revision}#{incoming.LegacyLevel}",
                     $"{incoming.StableId}@r{incoming.Revision}#{incoming.LegacyLevel}",
                 })
        {
            if (!string.Equals(legacy, current, StringComparison.Ordinal)
                && !keys.Contains(legacy, StringComparer.Ordinal))
            {
                keys.Add(legacy);
            }
        }

        if (storedById.TryGetValue(rule.Id.Trim(), out var previous))
        {
            foreach (var key in previous.LegacyRuleKeys)
            {
                if (!keys.Contains(key, StringComparer.Ordinal))
                {
                    keys.Add(key);
                }
            }
        }

        return keys.Count == rule.LegacyRuleKeys.Count ? rule : rule with { LegacyRuleKeys = keys };
    }

    /// <summary>
    /// One revision bump per edit, and never backwards: a caller that hands back a stale copy must
    /// not roll the stored revision down, because the revision is part of the key.
    /// </summary>
    private static FxAlertRule SettleRevision(
        FxAlertRule assigned,
        Dictionary<string, FxAlertRule> storedById)
    {
        if (!storedById.TryGetValue(assigned.Id.Trim(), out var previous))
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
    /// What makes two rules the same watch for migration purposes: every field that decides WHEN it
    /// fires (kind, currencies, bounds, thresholds, cost model), formatted invariantly. This is what
    /// lets a rule keep its identity across a reorder or a neighbouring removal — the list position
    /// only decides between rules the store genuinely cannot tell apart.
    /// </summary>
    private static string Signature(FxAlertRule rule) => string.Join(
        '\u0001',
        rule.DerivedId,
        Dec(rule.Lower),
        Dec(rule.Upper),
        Dec(rule.ThresholdPct),
        Dec(rule.ThresholdAmount),
        Dec(rule.SpreadPct),
        Dec(rule.FixedFee));

    private static string Dec(decimal? value) =>
        value?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

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