using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>
/// One canonical split emitted by <see cref="GenericLayoutPlan.Normalize"/>.
/// A record so two normalized plans compare by value — that is what makes
/// idempotence a testable property instead of a comment.
/// </summary>
public sealed record GenericSplitPlan(
    string TargetId,
    SplitDir Direction,
    float Ratio,
    string NewPanelId,
    byte Gap);

/// <summary>
/// Normalized layout description for a generic screen: one root id plus an
/// ordered list of splits that seat every other node exactly once.
/// </summary>
/// <remarks>
/// Derived from <see cref="GenericNodeSpec"/> plus <see cref="GenericSplitSpec"/>.
/// Normalization is a fixpoint: feeding <see cref="ToSpecs"/> back into
/// <see cref="Normalize"/> yields the same plan. Degenerate specs (a NaN or
/// out-of-range ratio, a node seated twice, a split whose target is not seated
/// yet) are resolved deterministically instead of throwing mid-build. This is
/// the spec-level half of the normalize/reconcile pair: the tree-level half is
/// <see cref="LayoutTree.Normalize"/>.
/// </remarks>
public sealed class GenericLayoutPlan
{
    private GenericLayoutPlan(string rootId, GenericSplitPlan[] splits, string[] nodeIds)
    {
        RootId = rootId;
        Splits = splits;
        NodeIds = nodeIds;
    }

    /// <summary>Id of the root node (the first declaration).</summary>
    public string RootId { get; }

    /// <summary>Canonical splits in seating order.</summary>
    public IReadOnlyList<GenericSplitPlan> Splits { get; }

    /// <summary>Node ids in declaration order.</summary>
    public IReadOnlyList<string> NodeIds { get; }

    /// <summary>
    /// Produces the canonical plan. Every node but the root is seated exactly
    /// once; nodes not seated by an explicit split get a deterministic default
    /// vertical split from the root, in declaration order.
    /// </summary>
    /// <exception cref="ArgumentException">Nodes are empty, or an id is blank or duplicated.</exception>
    /// <exception cref="KeyNotFoundException">A split names an unknown node.</exception>
    public static GenericLayoutPlan Normalize(
        IReadOnlyList<GenericNodeSpec> nodes,
        IReadOnlyList<GenericSplitSpec>? splits = null)
    {
        Ensure.NotNull(nodes);
        if (nodes.Count == 0)
        {
            throw new ArgumentException("At least one node is required.", nameof(nodes));
        }

        var byId = new Dictionary<string, GenericNodeSpec>(StringComparer.Ordinal);
        var order = new string[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            Ensure.NotNull(node);
            Ensure.NotBlank(node.Id);
            if (!byId.TryAdd(node.Id, node))
            {
                throw new ArgumentException($"Duplicate node id '{node.Id}'.", nameof(nodes));
            }

            order[i] = node.Id;
        }

        string rootId = order[0];
        var seated = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var result = new List<GenericSplitPlan>(nodes.Count - 1);

        if (splits is not null)
        {
            for (int i = 0; i < splits.Count; i++)
            {
                var split = splits[i];
                Ensure.NotNull(split);
                if (!byId.ContainsKey(split.TargetId))
                {
                    throw new KeyNotFoundException($"split targets unknown node '{split.TargetId}'");
                }

                if (!byId.ContainsKey(split.NewPanelId))
                {
                    throw new KeyNotFoundException($"split seats unknown node '{split.NewPanelId}'");
                }

                // Stable identity: a node is seated once. A repeated seat, or a
                // split whose target is not seated yet, is dropped here and the
                // node is seated by the deterministic default pass below.
                if (seated.Contains(split.NewPanelId) || !seated.Contains(split.TargetId))
                {
                    continue;
                }

                result.Add(new GenericSplitPlan(
                    split.TargetId,
                    split.Direction,
                    LayoutTree.CanonicalRatio(split.Ratio),
                    split.NewPanelId,
                    split.Gap));
                seated.Add(split.NewPanelId);
            }
        }

        for (int i = 1; i < order.Count; i++)
        {
            string id = order[i];
            if (seated.Contains(id))
            {
                continue;
            }

            result.Add(new GenericSplitPlan(
                rootId,
                SplitDir.Vertical,
                1f - (i / (float)(order.Count + 1)),
                id,
                0));
            seated.Add(id);
        }

        return new GenericLayoutPlan(rootId, result.ToArray(), order);
    }

    /// <summary>Round-trips the plan back into spec form (the idempotence proof).</summary>
    public IReadOnlyList<GenericSplitSpec> ToSpecs()
    {
        var specs = new GenericSplitSpec[Splits.Count];
        for (int i = 0; i < Splits.Count; i++)
        {
            var split = Splits[i];
            specs[i] = new GenericSplitSpec
            {
                TargetId = split.TargetId,
                Direction = split.Direction,
                Ratio = split.Ratio,
                NewPanelId = split.NewPanelId,
                Gap = split.Gap,
            };
        }

        return specs;
    }
}
