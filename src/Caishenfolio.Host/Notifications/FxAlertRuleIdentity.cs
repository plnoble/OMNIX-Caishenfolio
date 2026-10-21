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

        var changed = WatchChanged(existing, settled);
        // A real edit advances the revision, which renames the rule's key: the cooldown record of the
        // condition the user has changed away from is NOT carried over, so the new condition can
        // speak (review #C5-2). Toggling Enabled is not a watch change and keeps both.
        return changed
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

        // ONE set of identities this batch has spoken for: the ones it states explicitly, plus every one it
        // hands out (by signature match, by inheritance, or fresh). Two separate pools drift apart, and
        // drifting apart is exactly what let the allocator mint the same id twice (review #C4-1/#C4-2).
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in incoming)
        {
            if (!string.IsNullOrWhiteSpace(rule.Id))
            {
                taken.Add(rule.Id.Trim());
            }
        }

        var cursor = new Dictionary<string, int>(StringComparer.Ordinal);
        var settled = new List<FxAlertRule>(incoming.Count);
        foreach (var rule in incoming)
        {
            var assigned = string.IsNullOrWhiteSpace(rule.Id)
                ? AdoptForMigration(rule, storedBySignature, cursor, taken)
                : rule;

            assigned = SettleRevision(assigned, storedById);
            taken.Add(assigned.Id.Trim());
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
        HashSet<string> taken)
    {
        var signature = Signature(rule);
        if (storedBySignature.TryGetValue(signature, out var matches))
        {
            var start = cursor.GetValueOrDefault(signature);
            for (var i = start; i < matches.Count; i++)
            {
                if (taken.Contains(matches[i].Id.Trim()))
                {
                    continue;
                }

                cursor[signature] = i + 1;
                var match = matches[i];
                return rule with
                {
                    Id = match.Id.Trim(),
                    Revision = match.Revision,
                };
            }
        }

        // The inherited identity is only handed out if this batch has not spoken for it: the same set
        // that holds the explicit ids, so a half-migrated batch cannot mint it twice.
        return taken.Contains(rule.DerivedId) ? AssignNewIdentity(rule) : AdoptInheritedIdentity(rule);
    }

    /// <summary>
    /// The "rule part" of the key this rule uses right now:
    /// <c>{identity}@r{revision}#{level}</c>.
    /// </summary>
    private static string CurrentSuffix(FxAlertRule rule) =>
        $"{rule.StableId}@r{rule.Revision}#{rule.Level}";

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