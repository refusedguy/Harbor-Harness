using Harbor.Abstractions.Git;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Panels;

// ── jump (centred modal overlay, type/Backspace/Up/Down/Enter/Esc/r) ─────────

/// <summary>
///     Cell-native worktree jump palette (KILLER_FEATURES §2.7 Feature 3, slice 3):
///     lists <see cref="WorktreeJumpEntry.RowText" /> rows from
///     <see cref="WorktreeJumpPaletteModel" /> with the selected row marked
///     <c>▸</c> (same row-list idiom as the sibling builtin panels).
///     <c>Up</c>/<c>Down</c> move the selection, printable keys feed
///     <see cref="WorktreeJumpPaletteModel.SetQuery" /> (the fuzzy filter was
///     dead code until #381), <c>Backspace</c> trims the query,
///     <c>Enter</c> switches to the selected session via the existing
///     <see cref="IPanelSessionGateway.OpenPanelSessionAsync" /> (no new switching
///     mechanics), <c>Esc</c> closes and clears the query, <c>r</c> re-seeds.
/// </summary>
/// <remarks>
///     <para>
///         <b>Presentation (#381):</b> <see cref="DefaultPlacement" /> is
///         <see cref="TuiPanelPlacement.Center" />, so the palette no longer
///         steals a Right dock slot — <c>ChatScreenPanelDock</c> skips
///         Center-placed providers. <c>CellForgeJumpPaletteOverlayLayer</c> is the
///         layer this provider would be seated on (an <c>IOverlayLayer</c>,
///         <c>IsModal</c> ⇒ input barrier) and needs no new z-layer system to do
///         it, but as of #858 no product code constructs or seats it. The palette
///         the user drives today is the command palette frame, keyed by
///         <c>ReplInputLoop.HandleKeyAsync</c> (#857).
///     </para>
///     <para>
///         <b>Input barrier:</b> every key <c>OnKey</c> handles returns
///         <see langword="true" /> (consumed), so a host never lets palette
///         typing reach the composer / transcript. Modified chars (Ctrl/Alt)
///         and control chars are deliberately <em>not</em> consumed — the bare
///         LF alias of Ctrl+J must keep toggling the palette closed.
///     </para>
///     <para>
///         <b>Seeding:</b> merges real worktrees
///         (<see cref="IGitQuery.ListWorktrees" />, the Domain port that
///         <c>ProcessGitQuery</c> implements in Application) with the
///         active sessions from <see cref="UiState.Chat.Sessions" /> enriched
///         read-only through the <see cref="IPanelSessionGateway" /> the host put on
///         <see cref="PanelServices" /> (#470 — no per-frame service lookup; an
///         absent gateway simply leaves the rows un-enriched, and so does an
///         absent git query, which leaves the worktree rows empty). Provider-local
///         structures are never mutated. The model + seed cache
///         are provider-local mutable state guarded by a small lock (same
///         compromise as <see cref="CellForgeFileTreePanel" />) so
///         <c>Build</c> (render thread) and <c>OnKey</c> (input thread) stay
///         thread-safe; <c>Build</c> only seeds on the first frame after open
///         (<c>!Visible</c>) or an explicit <c>r</c> refresh, never every frame
///         (spawning git per frame would stall rendering).
///     </para>
///     <para>
///         <b>#666: the spawn moved out.</b> This panel used to build a
///         <c>ProcessStartInfo</c> for <c>git worktree list --porcelain</c> in a
///         private static helper and hand the text to
///         <c>WorktreeJumpSeeder.ParsePorcelain</c> — a Presentation assembly
///         forking a process, behind a <c>Func&lt;string&gt;</c> field standing in for
///         a seam nobody had declared. It now asks <see cref="PanelServices.Git" />
///         for <see cref="GitWorktreeInfo" /> rows, which is the same port the
///         branch badge has used since #537. The <c>Func</c> field is gone rather
///         than kept as a test hook: with a real port a test injects a fake
///         <see cref="IGitQuery" />, which is a stronger claim than a delegate
///         returning whatever string the test likes.
///     </para>
/// </remarks>
public sealed class CellForgeJumpPalettePanel : IPanelProvider
{
    private readonly object _gate = new();
    private readonly WorktreeJumpPaletteModel _model;

    /// <summary>Create a panel with a fresh palette model (renderer registration path).</summary>
    public CellForgeJumpPalettePanel()
        : this(new WorktreeJumpPaletteModel())
    {
    }

    /// <summary>Create a panel over an explicit model (tests drive selection through it).</summary>
    internal CellForgeJumpPalettePanel(WorktreeJumpPaletteModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>
    ///     Directory the worktree listing is taken from. Defaults to the process
    ///     working directory, which is what the panel asked git about before
    ///     <c>IGitQuery</c> existed and what the jump palette means by "here";
    ///     tests override it so a fake port can assert the directory it was given.
    /// </summary>
    /// <remarks>
    ///     The port moved the spawn, not the question: the old private helper
    ///     passed <see cref="Environment.CurrentDirectory" /> as the git working
    ///     directory, and this is that same value, now on the panel where a test
    ///     can reach it.
    /// </remarks>
    internal string WorktreeDirectory { get; set; } = Environment.CurrentDirectory;

    /// <inheritdoc />
    public string Id => OverlayIds.JumpPalette;

    /// <inheritdoc />
    public string Title => "Jump";

    /// <summary>
    ///     Centred modal overlay, not a dock leaf: the palette floats over the
    ///     chat band (Cmd-J palette UX) and
    ///     <see cref="CellForgeJumpPaletteOverlayLayer" /> seats it as a modal
    ///     <c>IOverlayLayer</c>. <c>ChatScreenPanelDock</c> skips
    ///     Center-placed providers, so the Right dock slot is released.
    /// </summary>
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Center;

    /// <summary>Overlay width in columns (the centred box caps itself to the viewport).</summary>
    public int DefaultSize => 48;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<WorktreeJumpEntry> snapshot;
        int selected;
        string query;
        lock (_gate)
        {
            if (!_model.Visible)
            {
                SeedLocked(ctx);
            }

            snapshot = new List<WorktreeJumpEntry>(_model.Results);
            selected = _model.SelectedIndex;
            query = _model.Query;
        }

        var rows = new List<string>(snapshot.Count + 4);
        // The query is part of the header so the fuzzy filter is visible (#381)
        // — an empty query keeps the pre-#381 "Jump (N)" shape byte-identical.
        rows.Add(query.Length == 0 ? $"Jump ({snapshot.Count})" : $"Jump: {query} ({snapshot.Count})");
        rows.Add(PanelText.Separator);
        if (snapshot.Count == 0)
        {
            rows.Add(query.Length == 0 ? "No worktrees or sessions." : "No match.");
            rows.Add("Open a session to jump between worktrees.");
        }
        else
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                string marker = i == selected ? "▸" : " ";
                rows.Add($"{marker} {snapshot[i].RowText}");
            }
        }

        rows.Add(PanelText.Separator);
        rows.Add("type to filter · ⌫ erase · ↑↓ move · Enter switch · Esc close · r refresh");
        return PanelText.Clip(rows, ctx.Width, ctx.Height);
    }

    /// <inheritdoc />
    public bool OnKey(UiKey key, PanelContext ctx)
    {
        switch (key.Code)
        {
            case UiKeyCode.Up:
                lock (_gate)
                {
                    _model.MoveUp();
                }

                return true;
            case UiKeyCode.Down:
                lock (_gate)
                {
                    _model.MoveDown();
                }

                return true;
            case UiKeyCode.Backspace:
                lock (_gate)
                {
                    _model.SetQuery(TrimLastChar(_model.Query));
                }

                return true;
            case UiKeyCode.Enter:
                ConfirmLocked(ctx);
                return true;
            case UiKeyCode.Escape:
                lock (_gate)
                {
                    // Hide() drops the query too, so reopening starts empty.
                    _model.Hide();
                }

                HideViaStore(ctx);
                return true;
            case UiKeyCode.Char when IsTypable(key):
            {
                char c = key.Character!.Value;

                // 'r' stays the documented refresh chord AND types normally:
                // re-seed first (preserving the typed prefix), then append.
                if (c is 'r' or 'R')
                {
                    lock (_gate)
                    {
                        ReseedLocked(ctx);
                    }
                }

                lock (_gate)
                {
                    _model.SetQuery(_model.Query + c);
                }

                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    ///     A key that belongs to the palette's query line: a <c>Char</c> with
    ///     no Ctrl/Alt modifier and a non-control character. Ctrl/Alt chords stay
    ///     unconsumed so the host keymap still sees them (the bare-LF alias of
    ///     Ctrl+J must keep toggling the palette closed, Alt+<c>n</c> must keep
    ///     switching panel slots).
    /// </summary>
    private static bool IsTypable(UiKey key) =>
        key.Character is { } c
        && key.Mods.AcceptsTypedChar()
        && !char.IsControl(c);

    /// <summary>Drops the last code point (surrogate-pair safe).</summary>
    private static string TrimLastChar(string query)
    {
        if (query.Length == 0)
        {
            return string.Empty;
        }

        int cut = query.Length - 1;
        if (cut > 0 && char.IsLowSurrogate(query[cut]) && char.IsHighSurrogate(query[cut - 1]))
        {
            cut--;
        }

        return query[..cut];
    }

    private void ConfirmLocked(PanelContext ctx)
    {
        WorktreeJumpEntry? selected;
        lock (_gate)
        {
            selected = _model.Confirm();
            _model.Hide();
        }

        // Worktree-only rows carry an empty SessionId (no session to switch
        // to) — just close the palette.
        if (selected is not null && !string.IsNullOrEmpty(selected.SessionId))
        {
            if (ctx.Deps.Sessions is { } gateway)
            {
                // #201: fire-and-forget through the shared helper — the fault is
                // observed (§FP-006) via OnlyOnFaulted. OpenSessionAsync logs
                // switch failures internally and returns false.
                TaskFireAndForget.Forget(gateway.OpenPanelSessionAsync(selected.SessionId));
            }
        }

        HideViaStore(ctx);
    }

    private void HideViaStore(PanelContext ctx)
    {
        if (ctx.Deps.Store is { } store)
        {
            _ = store.Dispatch(new AppMsg.TogglePanel(Id));
        }
    }

    /// <summary>Feed the model from sessions + worktrees. Call only under <c>_gate</c>.</summary>
    private void SeedLocked(PanelContext ctx)
    {
        var sessions = ctx.Deps.Sessions;
        IReadOnlyList<GitWorktreeInfo> worktrees = ReadWorktrees(ctx);
        _model.Show(WorktreeJumpSeeder.BuildEntries(SessionSeeds(ctx, sessions), worktrees));
    }

    /// <summary>
    ///     The repository's linked worktrees, through the Domain port. A host with
    ///     no git query registered lists sessions only; the panel never falls back
    ///     to forking <c>git</c> itself, which is the whole point of #666.
    /// </summary>
    /// <remarks>
    ///     The <c>try</c>/<c>catch</c> is belt-and-braces, not the mechanism:
    ///     <see cref="IGitQuery.ListWorktrees" /> documents that it returns an empty
    ///     list for every expected failure. It is kept because a palette that throws
    ///     on a paint path takes the renderer down with it, and the old private
    ///     helper had the same guard for the same reason.
    /// </remarks>
    private IReadOnlyList<GitWorktreeInfo> ReadWorktrees(PanelContext ctx)
    {
        if (ctx.Deps.Git is not { } queries)
        {
            return Array.Empty<GitWorktreeInfo>();
        }

        try
        {
            return queries.ListWorktrees(WorktreeDirectory);
        }
        catch
        {
            return Array.Empty<GitWorktreeInfo>();
        }
    }

    /// <summary>
    ///     Explicit <c>r</c> refresh: re-read worktrees + live sessions while
    ///     keeping the typed filter, so a refresh never throws away the query.
    ///     Call only under <c>_gate</c>.
    /// </summary>
    private void ReseedLocked(PanelContext ctx)
    {
        string query = _model.Query;
        SeedLocked(ctx);
        if (query.Length > 0)
        {
            _model.SetQuery(query);
        }
    }

    // #470: the flat gateway calls below reproduce the exact precedence the
    // panel used to spell out against the session-context and cached-git
    // lookups, so no seeded row changes — only the source became explicit.
    private static List<SessionSeed> SessionSeeds(PanelContext ctx, IPanelSessionGateway? gateway)
    {
        var sessions = ctx.State.Chat.Sessions;
        var seeds = new List<SessionSeed>(sessions.Length);
        for (int i = 0; i < sessions.Length; i++)
        {
            var info = sessions[i];
            string id = info.SessionId.Value;
            string directory = gateway?.GetDirectory(id) ?? string.Empty;
            string? branch = gateway?.GetBranch(id);
            string status = gateway?.GetStatusText(id) ?? "idle";
            bool dirty = gateway?.GetIsDirty(id) ?? false;
            bool isSubagent = info.IsSubagent || gateway?.GetIsSubagent(id) == true;
            seeds.Add(new SessionSeed(id, info.Title, directory, branch, status, dirty, isSubagent));
        }

        return seeds;
    }
}
