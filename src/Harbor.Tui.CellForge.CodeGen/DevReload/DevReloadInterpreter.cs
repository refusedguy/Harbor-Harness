using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     DEV-ONLY re-interpreter for <c>.harbor-ui</c> markup (issue #2433).
///     It re-parses changed text with the one shared <see cref="MarkupParser"/>,
///     compares the emptied template trees (Dioxus-style template diff), and
///     either patches values on the retained element tree through the #2439
///     mutation batch or rebuilds that tree. A change to binding/handler logic
///     rebuilds and asks the host to restart.
/// </summary>
/// <remarks>
///     <para>
///         This type lives in the build-time tooling assembly, not in the
///         runtime. The CLI references that assembly only with
///         <c>HarborWithDslDev=true</c>, which an AOT publish switches off
///         together with the <c>HARBOR_WITH_DSL_DEV</c> symbol. No Roslyn
///         compilation, JIT or Subsecond engine is started at run time: the
///         interpreter reads text, builds IR and applies structural commands.
///     </para>
///     <para>
///         The retained tree is the #2439 <see cref="MutationBatchApplier"/>.
///         Its numeric ids are the same dense pre-order sequences
///         <c>BuildRoot</c> emits as cell-diff keys. A patch only issues
///         set-text/set-style commands against ids that already exist, so a
///         host's per-id view-model, focus and scroll state stays valid on
///         unchanged positions. A rebuild creates a fresh applier and drops
///         that state, mirroring a real template swap.
///     </para>
/// </remarks>
public sealed class DevReloadInterpreter
{
    private readonly Dictionary<int, ElementSessionState> _sessions = [];
    private DevLogChannel? _log;
    private MutationBatchApplier _applier;
    private MarkupParseResult _current;
    private SpecArena _currentArena;
    private string _signature;
    private string _checksum;

    private DevReloadInterpreter(
        MutationBatchApplier applier,
        MarkupParseResult current,
        string signature,
        string checksum)
    {
        _applier = applier;
        _current = current;
        _signature = signature;
        _checksum = checksum;
    }

    /// <summary>Parses the first markup version and builds its live tree.</summary>
    /// <returns>False with <paramref name="error"/> when the source does not parse.</returns>
    public static bool TryCreate(string markup, out DevReloadInterpreter? interpreter, out string error)
    {
        var normalized = StripComments(markup ?? string.Empty);
        var parsed = MarkupParser.Parse(normalized);
        if (!parsed.IsValid)
        {
            interpreter = null;
            error = Describe(parsed);
            return false;
        }

        var applier = new MutationBatchApplier();
        Build(applier, parsed.Arena);

        interpreter = new DevReloadInterpreter(
            applier,
            parsed,
            ComputeTemplateSignature(parsed.Arena),
            MarkupChecksum.Compute(markup ?? string.Empty));
        interpreter._currentArena = parsed.Arena;
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     DEV-ONLY graph entry point. Resolves the importing file together
    ///     with every file it imports, expands the resolved tree and builds
    ///     the initial live tree from all of them.
    /// </summary>
    /// <param name="texts">Path-to-text map for all markup files visible to the watcher.</param>
    /// <param name="rootPath">Path of the screen file that owns the tree.</param>
    /// <param name="interpreter">The interpreter owning the live tree.</param>
    /// <param name="error">Graph or syntax error, empty on success.</param>
    public static bool TryCreateFromGraph(
        IReadOnlyDictionary<string, string> texts,
        string rootPath,
        out DevReloadInterpreter? interpreter,
        out string error)
    {
        var sources = ToSources(texts);
        var graph = MarkupModuleResolver.Resolve(sources);
        if (graph.Findings.Count != 0)
        {
            interpreter = null;
            error = Describe(graph);
            return false;
        }

        var arena = MarkupModuleExpander.Expand(graph, rootPath);
        var applier = new MutationBatchApplier();
        Build(applier, arena);
        interpreter = new DevReloadInterpreter(
            applier,
            new MarkupParseResult(arena, []),
            ComputeTemplateSignature(arena),
            MarkupModuleResolver.GraphChecksum(sources));
        interpreter._currentArena = arena;
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Attaches the DEV-only diagnostic channel (#2437). The channel is
    ///     optional and never reaches a renderer: a reload writes one record
    ///     with its outcome, revision and reason, and nothing else.
    /// </summary>
    /// <param name="log">Channel to write to, or null to keep logging off.</param>
    public void AttachLog(DevLogChannel? log) => _log = log;

    /// <summary>Revision of the live element tree.</summary>
    public int Revision => _applier.Revision;

    /// <summary>Root element id of the live tree, or zero when it has no root.</summary>
    public int LiveRootId => _applier.RootId;

    /// <summary>The emptied-tree signature used to decide patch versus rebuild.</summary>
    public string TemplateSignature => _signature;

    /// <summary>SHA-256 of the normalized source text.</summary>
    public string SourceChecksum => _checksum;

    /// <summary>Whether the live tree still contains <paramref name="id"/>.</summary>
    public bool Contains(int id) => _applier.Contains(id);

    /// <summary>Text currently held by <paramref name="id"/> in the live tree.</summary>
    public string? TextOf(int id) => _applier.TextOf(id);

    /// <summary>Child ids of <paramref name="id"/> in the live tree, in order.</summary>
    public IReadOnlyList<int> ChildrenOf(int id) => _applier.ChildrenOf(id);

    /// <summary>Interned style id held by <paramref name="id"/>, if it has a style.</summary>
    public int? StyleIdOf(int id) => _applier.StyleIdOf(id);

    /// <summary>Whether <paramref name="id"/> listens to <paramref name="eventType"/>.</summary>
    public bool HasListener(int id, string eventType) => _applier.HasListener(id, eventType);

    /// <summary>Session state attached to <paramref name="id"/>, if any.</summary>
    public ElementSessionState? SessionOf(int id) =>
        _sessions.TryGetValue(id, out var state) ? state : null;

    /// <summary>
    ///     Attaches host state to a live element. Ignored when the id is not in
    ///     the tree, so a stale host reference cannot resurrect a dead element.
    /// </summary>
    public void SetSession(int id, ElementSessionState state)
    {
        if (_applier.Contains(id))
            _sessions[id] = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    ///     Re-interprets <paramref name="markup"/> and moves the live tree to
    ///     the new version. Parse errors reject the step and leave the tree and
    ///     all session state untouched.
    /// </summary>
    public ReloadResult Reload(string markup)
    {
        var result = ReloadCore(markup);
        string checksum = result.SourceChecksum.Length <= 8
            ? result.SourceChecksum
            : result.SourceChecksum[..8];
        _log?.Write("interpreter",
            $"{result.Outcome} rev={result.Revision} root={result.LiveRootId} checksum={checksum}: {result.Reason}");
        return result;
    }

    /// <summary>
    ///     Re-resolves the module graph and moves the live tree to the new
    ///     expanded arena. Changing any imported file therefore produces the
    ///     same patch/rebuild decision as editing the screen itself. A broken
    ///     graph rejects the step and leaves the live tree untouched.
    /// </summary>
    public ReloadResult ReloadFromGraph(
        IReadOnlyDictionary<string, string> texts,
        string rootPath)
    {
        var result = ReloadFromGraphCore(texts, rootPath);
        string checksum = result.SourceChecksum.Length <= 8
            ? result.SourceChecksum
            : result.SourceChecksum[..8];
        _log?.Write("interpreter",
            $"{result.Outcome} rev={result.Revision} root={result.LiveRootId} checksum={checksum}: {result.Reason}");
        return result;
    }

    private ReloadResult ReloadFromGraphCore(
        IReadOnlyDictionary<string, string> texts,
        string rootPath)
    {
        var sources = ToSources(texts);
        var graph = MarkupModuleResolver.Resolve(sources);
        if (graph.Findings.Count != 0)
            return RejectedGraph("graph rejected: " + Describe(graph));

        var fresh = MarkupModuleExpander.Expand(graph, rootPath);
        var signature = ComputeTemplateSignature(fresh);
        var checksum = MarkupModuleResolver.GraphChecksum(sources);
        var wrapper = new MarkupParseResult(fresh, []);
        bool logicChanged = LogicChanged(_current, wrapper);

        if (signature == _signature)
        {
            if (checksum == _checksum)
                return Snapshot(ReloadOutcome.Unchanged, signature, checksum, "source unchanged after normalization");

            if (logicChanged)
            {
                return Rebuild(wrapper, signature, checksum, ReloadOutcome.RestartRequired,
                    "logic changed; tree rebuilt and host restart requested");
            }

            var patch = ComputePatch(_currentArena, fresh);
            var applied = _applier.Apply(patch);
            if (applied.IsFailure)
            {
                return Rebuild(wrapper, signature, checksum, ReloadOutcome.Rebuilt,
                    "patch rejected by the applier; rebuilt: " + applied.Error);
            }

            _current = wrapper;
            _currentArena = fresh;
            _signature = signature;
            _checksum = checksum;

            var preserved = new List<int>();
            foreach (int id in _sessions.Keys)
            {
                if (_applier.Contains(id))
                    preserved.Add(id);
            }

            preserved.Sort();
            return new ReloadResult(
                applied.Value.Dirty ? ReloadOutcome.Patched : ReloadOutcome.Unchanged,
                _applier.Revision,
                _applier.RootId,
                preserved,
                signature,
                checksum,
                applied.Value.Dirty ? "values patched in place on stable element ids" : "no value changed");
        }

        return Rebuild(
            wrapper,
            signature,
            checksum,
            logicChanged ? ReloadOutcome.RestartRequired : ReloadOutcome.Rebuilt,
            logicChanged
                ? "template and logic changed; tree rebuilt and host restart requested"
                : "template changed; tree rebuilt");
    }

    private ReloadResult RejectedGraph(string reason) =>
        new(
            ReloadOutcome.Rejected,
            _applier.Revision,
            _applier.RootId,
            [],
            _signature,
            _checksum,
            reason);

    private ReloadResult ReloadCore(string markup)
    {
        var normalized = StripComments(markup ?? string.Empty);
        var fresh = MarkupParser.Parse(normalized);
        if (!fresh.IsValid)
            return new ReloadResult(
                ReloadOutcome.Rejected,
                _applier.Revision,
                _applier.RootId,
                [],
                _signature,
                _checksum,
                "parse rejected: " + Describe(fresh));

        var signature = ComputeTemplateSignature(fresh.Arena);
        var checksum = MarkupChecksum.Compute(markup ?? string.Empty);
        bool logicChanged = LogicChanged(_current, fresh);

        if (signature == _signature)
        {
            // A comment-only or whitespace-only edit normalizes to the same text.
            if (checksum == _checksum)
                return Snapshot(ReloadOutcome.Unchanged, signature, checksum, "source unchanged after normalization");

            if (logicChanged)
                return Rebuild(fresh, signature, checksum, ReloadOutcome.RestartRequired,
                    "logic changed; tree rebuilt and host restart requested");

            var patch = ComputePatch(_currentArena, fresh.Arena);
            var applied = _applier.Apply(patch);
            if (applied.IsFailure)
                return Rebuild(fresh, signature, checksum, ReloadOutcome.Rebuilt,
                    "patch rejected by the applier; rebuilt: " + applied.Error);

            _current = fresh;
            _currentArena = fresh.Arena;
            _signature = signature;
            _checksum = checksum;

            // Ids are stable across a template-matched patch: keep every session
            // whose element survived (all of them, but check defensively).
            var preserved = new List<int>();
            foreach (int id in _sessions.Keys)
            {
                if (_applier.Contains(id))
                    preserved.Add(id);
            }

            preserved.Sort();
            return new ReloadResult(
                applied.Value.Dirty ? ReloadOutcome.Patched : ReloadOutcome.Unchanged,
                _applier.Revision,
                _applier.RootId,
                preserved,
                signature,
                checksum,
                applied.Value.Dirty ? "values patched in place on stable element ids" : "no value changed");
        }

        // Template shape changed. Rebuild in place; logic changes additionally
        // request a host restart because generated bindings would be stale.
        return Rebuild(
            fresh,
            signature,
            checksum,
            logicChanged ? ReloadOutcome.RestartRequired : ReloadOutcome.Rebuilt,
            logicChanged
                ? "template and logic changed; tree rebuilt and host restart requested"
                : "template changed; tree rebuilt");
    }

    /// <summary>
    ///     Canonical identity of the static template: kinds, nesting and
    ///     operation names, with every argument value emptied. Comments were
    ///     already removed by <see cref="StripComments"/> and the parser, so
    ///     the signature changes only when the shape or the operation set does.
    /// </summary>
    internal static string ComputeTemplateSignature(SpecArena arena)
    {
        var output = new StringBuilder();
        foreach (var root in arena.Roots)
            AppendSignature(root, output);
        return output.ToString();
    }

    private static void AppendSignature(SpecNode node, StringBuilder output)
    {
        output.Append('(').Append((int)node.Kind).Append(':');
        foreach (var operation in node.Operations)
            output.Append(operation.Name).Append(';');
        output.Append('[');
        foreach (var child in node.Children)
            AppendSignature(child, output);
        output.Append("])");
    }

    /// <summary>
    ///     Removes full-line comments while keeping line numbers intact. The
    ///     parser already ignores them; normalizing here means a comment-only
    ///     edit yields the same checksum and the same template signature.
    /// </summary>
    internal static string StripComments(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            int column = 0;
            while (column < line.Length && line[column] == ' ')
                column++;
            if (column < line.Length && line[column] == '#')
                lines[index] = string.Empty;
        }

        return string.Join("\n", lines);
    }

    private static void Build(MutationBatchApplier applier, SpecArena arena)
    {
        // #2439 lowering owns the structural commands and the .text / style
        // commands. It skips the literal of a `text "literal"` node (the same
        // literal BuildRootEmitter reads), so fill that value in explicitly.
        var lowered = SpecArenaMutationLowering.Lower(arena);
        var commands = new List<MutationCommand>(lowered.Commands);
        var texts = new MutationQueue();
        for (int index = 0; index < arena.Nodes.Count; index++)
        {
            var literal = TextLiteral(arena.Nodes[index]);
            if (literal is not null)
                texts.Enqueue(MutationCommand.SetText(index + 1, literal));
        }

        commands.AddRange(texts.Drain().Commands);
        var applied = applier.Apply(new MutationBatch(commands));
        if (applied.IsFailure)
            throw new InvalidOperationException("A lowered markup tree failed its own batch: " + applied.Error);
    }

    private static MutationBatch ComputePatch(SpecArena before, SpecArena after)
    {
        var queue = new MutationQueue();
        int count = Math.Min(before.Nodes.Count, after.Nodes.Count);
        for (int index = 0; index < count; index++)
        {
            int id = index + 1; // arena order is the stable sequence number
            var oldNode = before.Nodes[index];
            var newNode = after.Nodes[index];

            var oldText = TextLiteral(oldNode);
            var newText = TextLiteral(newNode);
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                queue.Enqueue(MutationCommand.SetText(id, newText ?? string.Empty));

            var oldStyle = StyleProperties(oldNode);
            var newStyle = StyleProperties(newNode);
            if (!StylesEqual(oldStyle, newStyle))
                queue.Enqueue(MutationCommand.SetStyle(id, MutationStyle.Create(newStyle ?? [])));
        }

        return queue.Drain();
    }

    private ReloadResult Rebuild(
        MarkupParseResult fresh,
        string signature,
        string checksum,
        ReloadOutcome outcome,
        string reason)
    {
        var applier = new MutationBatchApplier();
        Build(applier, fresh.Arena);
        _applier = applier;
        _sessions.Clear(); // a new template cannot inherit host state by position
        _current = fresh;
        _currentArena = fresh.Arena;
        _signature = signature;
        _checksum = checksum;
        return Snapshot(outcome, signature, checksum, reason);
    }

    private ReloadResult Snapshot(ReloadOutcome outcome, string signature, string checksum, string reason) =>
        new(outcome, _applier.Revision, _applier.RootId, [], signature, checksum, reason);

    /// <summary>
    ///     The displayed text of a node: a <c>.text "literal"</c> operation
    ///     (what #2439 lowering emits) or the <c>new</c> literal of a
    ///     <c>text "literal"</c> node (what BuildRootEmitter emits).
    /// </summary>
    private static string? TextLiteral(SpecNode node)
    {
        foreach (var operation in node.Operations)
        {
            if (IsText(operation.Name)
                && operation.Arguments.Count > 0
                && operation.Arguments[0].Kind == SpecValueKind.String)
                return operation.Arguments[0].Text;
        }

        if (node.Kind == SpecKind.Text)
        {
            foreach (var operation in node.Operations)
            {
                if (IsNew(operation.Name)
                    && operation.Arguments.Count > 0
                    && operation.Arguments[0].Kind == SpecValueKind.String)
                    return operation.Arguments[0].Text;
            }
        }

        return null;
    }

    /// <summary>The style properties #2439 lowering would record for a node.</summary>
    private static (string Name, string Value)[]? StyleProperties(SpecNode node)
    {
        var properties = new List<(string, string)>();
        foreach (var operation in node.Operations)
        {
            if (IsNew(operation.Name) || IsText(operation.Name) || IsLogic(operation.Name))
                continue;
            if (operation.Arguments.Count == 0)
                continue;
            properties.Add((operation.Name, operation.Arguments[0].Text));
        }

        return properties.Count == 0 ? null : properties.ToArray();
    }

    private static bool StylesEqual((string Name, string Value)[]? before, (string Name, string Value)[]? after)
    {
        if (before is null || after is null)
            return before is null && after is null;
        return MutationStyle.Create(before).StructurallyEquals(MutationStyle.Create(after));
    }

    /// <summary>
    ///     True when a binding/handler input changed. Such a change needs a
    ///     host restart: regenerated delegates would be stale even though the
    ///     widget tree can be rebuilt locally.
    /// </summary>
    private static bool LogicChanged(MarkupParseResult before, MarkupParseResult after)
    {
        if (!string.Equals(before.StateType, after.StateType, StringComparison.Ordinal))
            return true;

        var oldNodes = before.Arena.Nodes;
        var newNodes = after.Arena.Nodes;
        int count = Math.Min(oldNodes.Count, newNodes.Count);
        for (int index = 0; index < count; index++)
        {
            var oldLogic = LogicValues(oldNodes[index]);
            var newLogic = LogicValues(newNodes[index]);
            if (!oldLogic.SequenceEqual(newLogic, StringComparer.Ordinal))
                return true;
        }

        // A different node count is a TEMPLATE change, not a logic change. The
        // template signature already catches it and triggers a plain rebuild.
        return false;
    }

    private static IReadOnlyList<string> LogicValues(SpecNode node)
    {
        var values = new List<string>();
        foreach (var operation in node.Operations)
        {
            if (!IsLogic(operation.Name))
                continue;
            values.Add(operation.Arguments.Count == 0
                ? operation.Name
                : operation.Name + "=" + operation.Arguments[0].Text);
        }

        return values;
    }

    private static bool IsNew(string name) => name.Equals("new", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string name) => name.Equals("text", StringComparison.OrdinalIgnoreCase);

    /// <summary>Operations that feed generated bindings or event handlers.</summary>
    private static bool IsLogic(string name) =>
        name.StartsWith("on_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("click", StringComparison.OrdinalIgnoreCase)
        || name.Equals("command", StringComparison.OrdinalIgnoreCase)
        || name.Equals("bind", StringComparison.OrdinalIgnoreCase)
        || name.Equals("handler", StringComparison.OrdinalIgnoreCase)
        || name.Equals("event", StringComparison.OrdinalIgnoreCase)
        || name.Equals("state", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MarkupModuleSource> ToSources(IReadOnlyDictionary<string, string> texts)
    {
        var sources = new List<MarkupModuleSource>(texts.Count);
        foreach (var (path, text) in texts)
            sources.Add(new MarkupModuleSource(path, text));
        return sources;
    }

    private static string Describe(MarkupParseResult parsed) =>
        string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Line}:{d.Column} {d.Message}"));

    private static string Describe(ModuleGraphResult graph) =>
        string.Join("; ", graph.Findings.Select(f =>
            $"{f.Path}:{f.Diagnostic.Line}:{f.Diagnostic.Column} {f.Diagnostic.Message}"));
}
