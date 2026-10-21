namespace Caishenfolio.Host.Notifications;

/// <summary>
/// Works out which cooldown records have to be renamed during an upgrade, and applies it.
/// </summary>
/// <remarks>
/// The mapping is derived from the rule's shape every time, so nothing has to be remembered between
/// runs and a failed write can simply be retried on the next one. The OLD key is derived from the
/// rule as it ARRIVED (before its identity was settled) and the NEW key from the rule as it is now:
/// <list type="bullet">
/// <item>a rule that arrived without an identity was recorded under its derived one with the frozen
/// six-decimal level (<c>{DerivedId}@r0#{frozen level}</c>) — the pre-WP-03 upgrade, and the form
/// both members of a formerly-colliding pair were recorded under, each with its own level;</item>
/// <item>a rule that already carried an identity was recorded under that identity, and only the
/// level format changed (<c>{Id}@r{Revision}#{frozen level}</c>).</item>
/// </list>
/// A candidate that is another rule's CURRENT key is skipped: that record belongs to that rule, and
/// moving it would both steal its history and stop it recording a real exit (review #C5-3).
/// </remarks>
public static class AlertStateKeyMigration
{
    /// <summary>
    /// The mappings implied by how these rules arrived (<paramref name="incoming"/>) and how they are
    /// identified now (<paramref name="settled"/>), which must be the same rules in the same order.
    /// </summary>
    public static IReadOnlyList<AlertKeyMapping> MappingsFor(
        IReadOnlyList<FxAlertRule> incoming,
        IReadOnlyList<FxAlertRule> settled)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(settled);
        if (incoming.Count != settled.Count)
        {
            throw new ArgumentException("规则列表必须一一对应。", nameof(settled));
        }

        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in settled)
        {
            currentKeys.Add(AlertGatekeeper.KeyOf(FxAlertRuleEvaluator.Probe(rule)));
        }

        var mappings = new List<AlertKeyMapping>();
        for (var i = 0; i < incoming.Count; i++)
        {
            var before = incoming[i];
            var after = settled[i];
            if (string.IsNullOrWhiteSpace(after.Id))
            {
                // Not settled (an upgrade postponed by a failed rewrite): its key is still the old
                // one, so there is nothing to rename and nothing to invent.
                continue;
            }

            // The identity it had BEFORE settling — which, for a rule that arrived without one, is
            // exactly the derived fallback the older build keyed it under.
            var identity = before.StableId;
            if (string.IsNullOrWhiteSpace(identity))
            {
                continue;
            }

            var probe = FxAlertRuleEvaluator.Probe(before);
            var from = $"{probe.Kind}|{probe.Symbol}|{probe.Title}"
                + $"|rule:{identity}@r{before.Revision}#{before.LegacyLevel}";
            var to = AlertGatekeeper.KeyOf(FxAlertRuleEvaluator.Probe(after));
            Add(mappings, currentKeys, from, to);
        }

        return mappings;
    }

    /// <summary>
    /// Renames the records. Returns a result rather than throwing so a caller that cannot afford to
    /// lose cooldown history can postpone the identity change instead.
    /// </summary>
    public static AlertKeyMigrationResult Apply(
        AlertNotificationStateStore store,
        IReadOnlyList<FxAlertRule> incoming,
        IReadOnlyList<FxAlertRule> settled)
    {
        ArgumentNullException.ThrowIfNull(store);
        var mappings = MappingsFor(incoming, settled);
        return mappings.Count == 0
            ? new AlertKeyMigrationResult { Succeeded = true }
            : store.RewriteKeys(mappings);
    }

    private static void Add(
        List<AlertKeyMapping> mappings,
        HashSet<string> currentKeys,
        string from,
        string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)
            || currentKeys.Contains(from)
            || mappings.Any(mapping => string.Equals(mapping.From, from, StringComparison.Ordinal)))
        {
            return;
        }

        mappings.Add(new AlertKeyMapping(from, to));
    }
}