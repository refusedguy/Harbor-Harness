using System.Diagnostics;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Sessions;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Tui.CellForge.Panels;

// ── jump (Right/48, Up/Down/Enter/Esc/r) ────────────────────────────────────

/// <summary>
///     Cell-native worktree jump palette (KILLER_FEATURES §2.7 Feature 3, slice 2):
///     lists <see cref="WorktreeJumpEntry.RowText" /> rows from
///     <see cref="WorktreeJumpPaletteModel" /> with the selected row marked
///     <c>▸</c> (same row-list idiom as the sibling builtin panels).
///     <c>Up</c>/<c>Down</c> move the selection, <c>Enter</c> switches to the
///     selected session via the existing <c>ISessionManager.OpenSessionAsync</c>
///     (no new switching mechanics), <c>Esc</c> closes, <c>r</c> re-seeds.
/// </summary>
/// <remarks>
///     Seeding merges real worktrees (<c>git worktree list --porcelain</c>,
///     parsed by <see cref="WorktreeJumpSeeder" />) with the active sessions
///     from <see cref="UiState.Sessions" /> enriched read-only via
///     <c>ISessionManager.GetContext</c> / <c>GetGitInfo</c> — provider-local
///     structures are never mutated. The model + seed cache are provider-local
///     mutable state guarded by a small lock (same compromise as
///     <see cref="CellForgeFileTreePanel" />) so <c>Build</c> (render thread)
///     and <c>OnKey</c> (input thread) stay thread-safe; <c>Build</c> only
///     seeds on the first frame after open (<c>!Visible</c>) or an explicit
///     <c>r</c> refresh, never every frame (spawning git per frame would
///     stall rendering).
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

    /// <inheritdoc />
    public TuiPanelPlacement DefaultPlacement => TuiPanelPlacement.Right;

    /// <inheritdoc />
    public int DefaultSize => 48;

    /// <inheritdoc />
    public object? Build(PanelContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<WorktreeJumpEntry> snapshot;
        int selected;
        lock (_gate)
        {
            if (!_model.Visible)
            {
                SeedLocked(ctx);
            }

            snapshot = new List<WorktreeJumpEntry>(_model.Results);
            selected = _model.SelectedIndex;
        }

        var rows = new List<string>(snapshot.Count + 4);
        rows.Add($"Jump ({snapshot.Count})");
        rows.Add(PanelText.Separator);
        if (snapshot.Count == 0)
        {
            rows.Add("No worktrees or sessions.");
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
        rows.Add("↑↓ move · Enter switch · Esc close · r refresh");
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
            case UiKeyCode.Enter:
                ConfirmLocked(ctx);
                return true;
            case UiKeyCode.Escape:
                lock (_gate)
                {
                    _model.Hide();
                }

                HideViaStore(ctx);
                return true;
            case UiKeyCode.Char when key.Character is 'r' or 'R':
                lock (_gate)
                {
                    _model.Hide();
                    SeedLocked(ctx);
                }

                return true;
            default:
                return false;
        }
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
                // OpenSessionAsync logs switch failures internally and returns
                // false; OnlyOnFaulted only observes the exception (§FP-006).
                _ = manager.OpenSessionAsync(selected.SessionId).ContinueWith(
                    static t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        HideViaStore(ctx);
    }

    private void HideViaStore(PanelContext ctx)
    {
        if (ctx.Services?.GetService<UiStore>() is UiStore store)
        {
            _ = store.Dispatch(new UiMsg.TogglePanel(Id));
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

    private static List<SessionSeed> SessionSeeds(PanelContext ctx, ISessionManager? manager)
    {
        var sessions = ctx.State.Sessions;
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
            seeds.Add(new SessionSeed(id, info.Title, directory, branch, status, dirty));
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
