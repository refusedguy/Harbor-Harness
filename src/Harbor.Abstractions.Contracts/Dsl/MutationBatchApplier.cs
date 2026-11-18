using System;
using System.Collections.Generic;
using CSharpFunctionalExtensions;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Applies a mutation batch in three ordered phases: validate every
///     command against a shadow of the tree, intern every style, then write to
///     the tree. Phases 1 and 2 are the only ones that can refuse a batch, and
///     neither touches the tree. Phase 3 has no failure paths.
/// </summary>
internal sealed class MutationBatchApplier
{
    private readonly RetainedElementTree _tree = new();
    private readonly StyleTable _styles;
    private int _revision;
    private bool _dirty;

    internal MutationBatchApplier(int styleLimit = 4096) => _styles = new StyleTable(styleLimit);

    internal int Revision => _revision;
    internal int InternedStyleCount => _styles.Count;
    internal int RootId => _tree.RootId;

    internal bool Contains(int id) => _tree.Contains(id);

    internal int? StyleIdOf(int id) => _tree.StyleIdOf(id);

    internal string? TextOf(int id) => _tree.TextOf(id);

    internal bool HasListener(int id, string eventType) => _tree.HasListener(id, eventType);

    internal IReadOnlyList<int> ChildrenOf(int id) => _tree.ChildrenOf(id);

    /// <summary>Returns the pending dirty flag and clears it, like take_dirty.</summary>
    internal bool TakeDirty()
    {
        bool dirty = _dirty;
        _dirty = false;
        return dirty;
    }

    internal Result<ApplyOutcome> Apply(MutationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!Validate(batch, out var error))
            return Result.Failure<ApplyOutcome>(error);

        var resolvedStyles = new int[batch.Count];
        int mark = _styles.Mark();
        for (int i = 0; i < batch.Count; i++)
        {
            var command = batch.Commands[i];
            if (command.Op != MutationOp.SetStyle)
                continue;
            if (!_styles.TryIntern(command.Style!, out resolvedStyles[i]))
            {
                _styles.Sweep(mark);
                return Result.Failure<ApplyOutcome>($"Phase 2: style table is full at command {i}.");
            }
        }

        bool changed = false;
        List<int>? freed = null;
        int styleIndex = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            var command = batch.Commands[i];
            bool changedHere;
            switch (command.Op)
            {
                case MutationOp.CreateElement:
                    changedHere = _tree.Create(command.Id, command.Kind);
                    break;
                case MutationOp.SetStyle:
                    changedHere = _tree.SetStyle(command.Id, resolvedStyles[styleIndex++]);
                    break;
                case MutationOp.SetText:
                    changedHere = _tree.SetText(command.Id, command.Text!);
                    break;
                case MutationOp.AppendChild:
                    changedHere = _tree.Append(command.Id, command.SecondaryId);
                    break;
                case MutationOp.InsertBefore:
                    changedHere = _tree.InsertBefore(command.Id, command.SecondaryId, command.ExtraId);
                    break;
                case MutationOp.SetEventListener:
                    changedHere = _tree.SetListener(command.Id, command.EventType!, command.Enabled);
                    break;
                case MutationOp.SetRoot:
                    changedHere = _tree.SetRoot(command.Id);
                    break;
                default:
                    (freed ??= []).AddRange(_tree.Destroy(command.Id));
                    changedHere = true;
                    break;
            }

            changed |= changedHere;
        }

        if (changed)
        {
            _revision++;
            _dirty = true;
        }

        return Result.Success(new ApplyOutcome(_revision, changed, freed?.ToArray() ?? []));
    }

    private bool Validate(MutationBatch batch, out string error)
    {
        var live = new HashSet<int>();
        var parents = new Dictionary<int, int>();
        _tree.CollectState(live, parents);

        for (int i = 0; i < batch.Count; i++)
        {
            var command = batch.Commands[i];
            switch (command.Op)
            {
                case MutationOp.CreateElement:
                    if (command.Id <= 0 || !live.Add(command.Id))
                    {
                        error = $"Phase 1: command {i} creates duplicate or invalid id {command.Id}.";
                        return false;
                    }

                    parents[command.Id] = 0;
                    break;

                case MutationOp.SetStyle:
                case MutationOp.SetText:
                case MutationOp.SetEventListener:
                    if (command.Id <= 0 || !live.Contains(command.Id))
                    {
                        error = $"Phase 1: command {i} targets unknown id {command.Id}.";
                        return false;
                    }

                    break;

                case MutationOp.AppendChild:
                    if (!BothLive(command, live) || command.Id == command.SecondaryId
                        || IsAncestor(command.SecondaryId, command.Id, parents))
                    {
                        error = $"Phase 1: command {i} would create a cycle or link a missing id.";
                        return false;
                    }

                    parents[command.SecondaryId] = command.Id;
                    break;

                case MutationOp.InsertBefore:
                    if (!live.Contains(command.ExtraId) || parents[command.ExtraId] != command.Id
                        || command.SecondaryId == command.ExtraId || command.Id == command.SecondaryId
                        || !live.Contains(command.SecondaryId)
                        || IsAncestor(command.SecondaryId, command.Id, parents))
                    {
                        error = $"Phase 1: command {i} has an invalid before target.";
                        return false;
                    }

                    parents[command.SecondaryId] = command.Id;
                    break;

                case MutationOp.SetRoot:
                    if (!live.Contains(command.Id))
                    {
                        error = $"Phase 1: command {i} sets an unknown root id {command.Id}.";
                        return false;
                    }

                    break;

                case MutationOp.DestroyElement:
                    if (!live.Contains(command.Id))
                    {
                        error = $"Phase 1: command {i} destroys unknown id {command.Id}.";
                        return false;
                    }

                    RemoveSubtree(command.Id, live, parents);
                    break;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool BothLive(MutationCommand command, HashSet<int> live) =>
        live.Contains(command.Id) && live.Contains(command.SecondaryId);

    private static bool IsAncestor(int candidate, int node, Dictionary<int, int> parents)
    {
        int current = node;
        while (true)
        {
            if (current == candidate)
                return true;
            if (!parents.TryGetValue(current, out int parent) || parent == 0)
                return false;
            current = parent;
        }
    }

    private static void RemoveSubtree(int root, HashSet<int> live, Dictionary<int, int> parents)
    {
        var doomed = new List<int>();
        foreach (int id in parents.Keys)
        {
            int current = id;
            while (true)
            {
                if (current == root)
                {
                    doomed.Add(id);
                    break;
                }

                if (!parents.TryGetValue(current, out int parent) || parent == 0)
                    break;
                current = parent;
            }
        }

        foreach (int id in doomed)
        {
            live.Remove(id);
            parents.Remove(id);
        }
    }
}
