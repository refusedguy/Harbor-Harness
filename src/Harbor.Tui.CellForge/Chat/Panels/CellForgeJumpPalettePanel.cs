using System.Diagnostics;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

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
///     <c>ISessionManager.OpenSessionAsync</c> (no new switching mechanics),
///     <c>Esc</c> closes and clears the query, <c>r</c> re-seeds.
/// </summary>
/// <remarks>
///     <para>
///         <b>Presentation (#381):</b> <see cref="DefaultPlacement" /> is
///         <see cref="TuiPanelPlacement.Center" />, so the palette no longer
///         steals a Right dock slot — <c>ChatScreenPanelDock</c> skips
///         Center-placed providers and the host seats
///         <c>CellForgeJumpPaletteOverlayLayer</c> (an <c>IOverlayLayer</c>,
///         <c>IsModal</c> ⇒ input barrier) on the existing
///         <c>LayoutTree.Overlays</c> stack. No new z-layer system.
///     </para>
///     <para>
///         <b>Input barrier:</b> every key <c>OnKey</c> handles returns
///         <see langword="true" /> (consumed), so a host never lets palette
///         typing reach the composer / transcript. Modified chars (Ctrl/Alt)
///         and control chars are deliberately <em>not</em> consumed — the bare
///         LF alias of Ctrl+J must keep toggling the palette closed.
///     </para>
///     <para>
///         <b>Seeding:</b> merges real worktrees (<c>git worktree list
///         --porcelain</c>, parsed by <see cref="WorktreeJumpSeeder" />) with
///         the active sessions from <see cref="UiState.Chat.Sessions" /> enriched
///         read-only via <c>ISessionManager.GetContext</c> / <c>GetGitInfo</c> —
///         provider-local structures are never mutated. The model + seed cache
///         are provider-local mutable state guarded by a small lock (same
///         compromise as <see cref="CellForgeFileTreePanel" />) so
///         <c>Build</c> (render thread) and <c>OnKey</c> (input thread) stay
///         thread-safe; <c>Build</c> only seeds on the first frame after open
///         (<c>!Visible</c>) or an explicit <c>r</c> refresh, never every frame
///         (spawning git per frame would stall rendering).
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
    ///     Raw <c>git worktree list --porcelain</c> output source. Defaults to
    ///     spawning git in the current directory (3s timeout, empty on any
    ///     failure); tests override it for hermetic seeding.
    /// </summary>
    internal Func<string> WorktreePorcelainReader { get; set; } = ReadWorktreePorcelain;

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
        && key.Mods is KeyModifierSet.None or KeyModifierSet.Shift
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
            if (ctx.Services?.GetService<ISessionManager>() is ISessionManager manager)
            {
                // #201: fire-and-forget through the shared helper — the fault is
                // observed (§FP-006) via OnlyOnFaulted. OpenSessionAsync logs
                // switch failures internally and returns false.
                TaskFireAndForget.Forget(manager.OpenSessionAsync(selected.SessionId));
            }
        }

        HideViaStore(ctx);
    }

    private void HideViaStore(PanelContext ctx)
    {
        if (ctx.Services?.GetService<UiStore>() is UiStore store)
        {
            _ = store.Dispatch(new AppMsg.TogglePanel(Id));
        }
    }

    /// <summary>Feed the model from sessions + worktrees. Call only under <c>_gate</c>.</summary>
    private void SeedLocked(PanelContext ctx)
    {
        var manager = ctx.Services?.GetService<ISessionManager>();
        IReadOnlyList<WorktreeInfo> worktrees;
        try
        {
            worktrees = WorktreeJumpSeeder.ParsePorcelain(WorktreePorcelainReader());
        }
        catch
        {
            worktrees = Array.Empty<WorktreeInfo>();
        }

        _model.Show(WorktreeJumpSeeder.BuildEntries(SessionSeeds(ctx, manager), worktrees));
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

    private static List<SessionSeed> SessionSeeds(PanelContext ctx, ISessionManager? manager)
    {
        var sessions = ctx.State.Chat.Sessions;
        var seeds = new List<SessionSeed>(sessions.Length);
        for (int i = 0; i < sessions.Length; i++)
        {
            var info = sessions[i];
            string id = info.SessionId.Value;
            var context = manager?.GetContext(id);
            var git = manager is not null ? manager.GetGitInfo(id) : null;
            string directory = context?.Session.Directory ?? string.Empty;
            string? branch = git?.Branch ?? context?.Session.GitBranch ?? context?.GitBranch;
            string status = context?.StatusText ?? "idle";
            bool dirty = (git?.IsDirty ?? false)
                || (context?.GitIsDirty ?? false)
                || (context?.Session.GitIsDirty ?? false);
            bool isSubagent = info.IsSubagent || context?.Session.IsSubagent() == true;
            seeds.Add(new SessionSeed(id, info.Title, directory, branch, status, dirty, isSubagent));
        }

        return seeds;
    }

    private static string ReadWorktreePorcelain()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("worktree");
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("--porcelain");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return string.Empty;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Process already exited.
                }

                return string.Empty;
            }

            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
