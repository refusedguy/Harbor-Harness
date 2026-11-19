// #3084: the product view : State -> Tree envelope. The .harbor-ui template
// in this folder is compiled by the markup generator into a static BuildRoot
// whose sequence numbers are stable element identities. Each Sync runs that
// generated body, resolves its text slots from the TEA state, lowers the result
// to a MutationBatch and applies it to a retained element tree. State-tree
// values (focus, scroll) live in ElementStateStore keyed by the same stable id,
// so a value-only state change keeps them across reconciliations.
//
// Everything here is ordinary C#: the generator is a build-time analyzer and
// no Roslyn, JIT or reflection type is named on this path.

using System;
using System.Collections.Generic;
using System.Globalization;
using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen.Generated;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Generic;

/// <summary>Per-widget state kept across frames by <see cref="ElementStateStore"/>.</summary>
public sealed class StateElementState
{
    /// <summary>Whether this widget currently owns keyboard focus.</summary>
    public bool Focused { get; set; }

    /// <summary>Scroll offset kept between redraws.</summary>
    public int ScrollOffset { get; set; }
}

/// <summary>
///     Reconciles a generated CellForge root against <see cref="UiState"/>.
///     The first <see cref="Sync"/> builds the retained tree; later ones issue
///     only the text mutations that actually changed and keep element state.
///     Render-thread owned, like the rest of the engine.
/// </summary>
public sealed class StateTreeView
{
    private MutationBatchApplier _applier = new();
    private readonly ElementStateStore _states = new();
    private readonly List<TreeNode> _nodes = [];
    private readonly List<TreeNode> _lines = [];
    private readonly Func<string, string> _resolveSnapshot;
    private string[] _lastOps = [];
    private string _focus = string.Empty;
    private string _mode = string.Empty;
    private int _lastMutationCount;
    private bool _built;
    private bool _hasSnapshot;

    /// <summary>Creates an empty view over the compiled product template.</summary>
    public StateTreeView() => _resolveSnapshot = ResolveSnapshot;

    /// <summary>Revision of the retained element tree after the last sync.</summary>
    public int Revision => _applier.Revision;

    /// <summary>Root element id of the retained tree, or zero before the first sync.</summary>
    public int RootId => _applier.RootId;

    /// <summary>Number of commands in the last applied mutation batch.</summary>
    public int LastMutationCount => _lastMutationCount;

    /// <summary>Operation names of the last applied mutation batch, in order.</summary>
    public IReadOnlyList<string> LastMutationOps => _lastOps;

    /// <summary>Whether the last sync patched values instead of rebuilding the tree.</summary>
    public bool LastWasIncremental { get; private set; }

    /// <summary>Number of nodes the generated root produced.</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>
    ///     Element state for one generated sequence number. The same id is
    ///     touched by <see cref="Render"/> every frame, so the slot survives
    ///     value-only reconciliations.
    /// </summary>
    public StateElementState ElementState(int sequence) =>
        _states.GetOrCreate<StateElementState>(IdOf(sequence));

    /// <summary>Element state for the n-th visible text line, zero based.</summary>
    public StateElementState ElementStateAtLine(int index) =>
        _states.GetOrCreate<StateElementState>(_lines[index].Id);

    private static ElementId IdOf(int sequence) =>
        ElementId.Root.Child("s" + (sequence + 1).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    ///     Projects <paramref name="state"/> into the retained tree. Token
    ///     deltas leave the projected strings untouched, so the common case is
    ///     a reference comparison and no allocation at all.
    /// </summary>
    /// <returns>False when the applier rejected a batch; the previous tree stays.</returns>
    public bool Sync(UiState state)
    {
        string focus = state.Ui.FocusedPanelId ?? string.Empty;
        string mode = state.Ui.Input.Text ?? string.Empty;
        if (_hasSnapshot
            && string.Equals(focus, _focus, StringComparison.Ordinal)
            && string.Equals(mode, _mode, StringComparison.Ordinal))
        {
            return true;
        }

        _focus = focus;
        _mode = mode;
        bool applied = SyncResolvedCore(_resolveSnapshot);
        if (applied)
            _hasSnapshot = true;
        return applied;
    }

    private string ResolveSnapshot(string key) => key switch
    {
        "focus" => _focus,
        "mode" => _mode,
        _ => key,
    };

    /// <summary>
    ///     Same reconciliation with an explicit text resolver. Used by hosts
    ///     that own a projection other than <see cref="UiState"/> and by the
    ///     regression tests, which vary one slot at a time.
    /// </summary>
    public bool SyncResolved(Func<string, string> resolve) => SyncResolvedCore(resolve);

    private bool SyncResolvedCore(Func<string, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        var sink = new RootSink(resolve);
        BuildCompiledRoot(sink);
        var fresh = sink.Nodes;

        bool incremental = _built && SameShape(fresh, _nodes);
        var batch = incremental ? Diff(fresh, _nodes) : Full(fresh);
        if (!incremental)
        {
            // A different template cannot inherit numeric ids or element state.
            _applier = new MutationBatchApplier();
            _states.Reset();
        }

        var outcome = _applier.Apply(batch);
        if (outcome.IsFailure)
            return false;

        var ops = new string[batch.Count];
        for (int i = 0; i < batch.Count; i++)
            ops[i] = batch.Commands[i].Op.ToString();
        _lastOps = ops;
        _lastMutationCount = batch.Count;
        LastWasIncremental = incremental;

        _nodes.Clear();
        _nodes.AddRange(fresh);
        RebuildLines();
        _built = true;
        return true;
    }

    /// <summary>
    ///     Drives one frame over the retained tree. The element-state frame is
    ///     bracketed here so every visible node touches its slot; EndFrame then
    ///     drops the slots of nodes that disappeared from the tree.
    /// </summary>
    public void Render(int cols, int rows, ScreenBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _states.BeginFrame();
        int y = 0;
        foreach (var line in _lines)
        {
            _ = _states.GetOrCreate<StateElementState>(line.Id);
            if (y >= Math.Max(0, rows))
                continue;

            var text = line.Text ?? string.Empty;
            int take = Math.Min(text.Length, Math.Max(0, cols));
            if (take > 0)
            {
                var style = default(CellStyle);
                buffer.SetText(0, y, text.AsSpan(0, take), in style);
            }
            y++;
        }
        _states.EndFrame();
    }

    /// <summary>Runs the checked-in BuildRoot body emitted by the generator.</summary>
    private static void BuildCompiledRoot(RootSink sink) =>
        HarborUi_Generic_StateTree_harbor_ui.BuildRoot(sink.Begin, sink.TestId, sink.Text, sink.End);

    private static bool SameShape(List<TreeNode> fresh, List<TreeNode> old)
    {
        if (fresh.Count != old.Count)
            return false;
        for (int i = 0; i < fresh.Count; i++)
        {
            if (fresh[i].Sequence != old[i].Sequence
                || fresh[i].Children.Count != old[i].Children.Count)
            {
                return false;
            }
        }
        return true;
    }

    private static MutationBatch Diff(List<TreeNode> fresh, List<TreeNode> old)
    {
        var commands = new List<MutationCommand>();
        for (int i = 0; i < fresh.Count && i < old.Count; i++)
        {
            int id = fresh[i].Sequence + 1;
            if (!string.Equals(fresh[i].Text, old[i].Text, StringComparison.Ordinal))
                commands.Add(MutationCommand.SetText(id, fresh[i].Text ?? string.Empty));
        }
        return new MutationBatch(commands);
    }

    private static MutationBatch Full(List<TreeNode> nodes)
    {
        var commands = new List<MutationCommand>(nodes.Count * 2);
        foreach (var node in nodes)
        {
            int id = node.Sequence + 1;
            commands.Add(MutationCommand.CreateElement(id, SpecKind.Panel));
            if (node.Text is not null)
                commands.Add(MutationCommand.SetText(id, node.Text));
        }

        foreach (var node in nodes)
        {
            foreach (var child in node.Children)
                commands.Add(MutationCommand.AppendChild(node.Sequence + 1, child.Sequence + 1));
        }

        if (nodes.Count > 0)
            commands.Add(MutationCommand.SetRoot(nodes[0].Sequence + 1));
        return new MutationBatch(commands);
    }

    private void RebuildLines()
    {
        _lines.Clear();
        foreach (var node in _nodes)
        {
            if (node.Text is not null)
                _lines.Add(node);
        }
    }

    /// <summary>One node of the tree the generated BuildRoot emitted.</summary>
    private sealed class TreeNode
    {
        internal TreeNode(int sequence, ElementId id)
        {
            Sequence = sequence;
            Id = id;
        }

        internal int Sequence { get; }
        internal ElementId Id { get; }
        internal string? Text { get; set; }
        internal string? TestId { get; set; }
        internal List<TreeNode> Children { get; } = [];
    }

    /// <summary>Receives the generated sequence-numbered calls into a tree.</summary>
    private sealed class RootSink
    {
        private readonly Func<string, string> _resolve;
        private readonly Stack<TreeNode> _stack = new();

        internal RootSink(Func<string, string> resolve) => _resolve = resolve;

        internal List<TreeNode> Nodes { get; } = [];

        internal void Begin(int sequence)
        {
            var node = new TreeNode(sequence, IdOf(sequence));
            Nodes.Add(node);
            if (_stack.Count > 0)
                _stack.Peek().Children.Add(node);
            _stack.Push(node);
        }

        internal void TestId(int sequence, string testId)
        {
            if (_stack.Count > 0)
                _stack.Peek().TestId = testId;
        }

        internal void Text(int sequence, string text)
        {
            if (_stack.Count > 0)
                _stack.Peek().Text = _resolve(text);
        }

        internal void End()
        {
            if (_stack.Count > 0)
                _stack.Pop();
        }
    }
}
