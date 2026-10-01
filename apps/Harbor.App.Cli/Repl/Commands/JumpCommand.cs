using Harbor.Abstractions.Git;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Overlays;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     Worktree / session jump palette — the fourth member of the family that
///     already ships three times over (<see cref="SessionTreeCommand" />,
///     <see cref="ProvidersCommand" />, <see cref="PluginsPanelCommand" />):
///     build a panel model, project its rows into <see cref="CommandItem" />s
///     and hand them to <see cref="IReplHost.Palette" />. Ctrl+J
///     (<c>ReplInputLoop</c>) dispatches this command, so the keystroke
///     reaches the one palette input path the REPL already keys directly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the palette axis and not the panel plane (issue #857).</b>
///         The jump palette also exists as
///         <c>CellForgeJumpPalettePanel</c> — a <c>Center</c>-placed
///         <c>IPanelProvider</c> whose <c>OnKey</c> no product host can reach,
///         so Ctrl+J toggled state nobody painted. Reaching it through the
///         panel plane would mean a third hand-written input path
///         (<c>OverlayStack.RouteKey</c> and <c>CellScreenPanelDock.RoutePanelKey</c>
///         are already unreachable for the same reason) and a
///         <c>Focused</c> precondition the product has no producer for. The
///         palette route needs neither: the frame stack, the query filter, the
///         ↑↓ selection and the Enter commit are already live, and the rows,
///         the fuzzy match and the session merge are already
///         <c>WorktreeJumpSeeder</c> / <c>WorktreeJumpPaletteModel</c> with
///         their own tests.
///     </para>
///     <para>
///         <b>No second fuzzy matcher.</b> Typing filters through
///         <c>CommandPaletteView.Refilter</c> → <c>FuzzyMatcher</c>, the same
///         matcher Ctrl+P uses; the rows are projected as-is. <c>PanelFuzzy</c>
///         is deliberately left alone: it is what the <c>SetQuery</c> path of
///         the panel models uses, and the palette route never calls
///         <c>SetQuery</c> — which is exactly how the three siblings behave.
///     </para>
///     <para>
///         <b>Where the rows come from.</b> The stored
///         <see cref="Session" /> already carries
///         <see cref="Session.Directory" />, <see cref="Session.GitBranch" />,
///         <see cref="Session.GitIsDirty" />, <see cref="Session.Status" />
///         and <see cref="Session.Kind" />, so the seeds are mapped from the
///         store directly and the CLI needs no <c>IPanelSessionGateway</c> — the
///         gateway is registered by the Avalonia host alone, which is why the
///         panel's own seeding degrades to empty directories and a null branch
///         under the CLI. Branch still falls back to the worktree matched by
///         directory inside <see cref="WorktreeJumpSeeder" />.
///     </para>
/// </remarks>
internal sealed class JumpCommand : IReplCommand
{
    /// <summary>
    ///     Breadcrumb of the frame this command pushes. Also the identity the
    ///     Ctrl+J chord matches on to close the palette again, so it is a
    ///     constant rather than a literal at each use.
    /// </summary>
    internal const string Breadcrumb = "worktrees / jump";

    public string Id => "jump";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Jump";
    public string Description => "jump to a worktree or session";
    public string Group => "Sessions";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var store = host.SessionStore;
        if (store is null)
        {
            host.Bridge.AppendSystemLine("⇄ переход недоступен: хост без хранилища сессий");
            host.WakeUp();
            return;
        }

        var listed = await store.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {listed.Error}");
            host.WakeUp();
            return;
        }

        var entries = WorktreeJumpSeeder.BuildEntries(Seeds(listed.Value), Worktrees(host));

        // `RowText` is "{title}  {branch}  {status}  {path}"; the palette
        // paints Title bold and Detail dimmed, so splitting the row at the
        // title reproduces the same two-tone line the panel drew. The group
        // separates rows that can be switched to from worktree-only rows,
        // which carry no session id and only close the palette on Enter.
        var items = new List<CommandItem>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool switchable = entry.SessionId.Length > 0;
            items.Add(new CommandItem(
                entry.SessionId,
                entry.Title,
                $"{entry.BranchText}  {entry.Status}  {entry.Path}",
                string.Empty,
                switchable ? "sessions" : "worktrees"));
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Jump", Breadcrumb, items,
            OnCommitAsync: (selected, frameCt) => SwitchAsync(host, selected, frameCt),
            // Seed order is the merge order (sessions, then unmatched
            // worktrees path-sorted); the default empty-query sort would
            // reshuffle it by group and title, and a worktree list whose rows
            // move when you open it is not a list you can read.
            PreserveOrder: true));
        host.WakeUp();
    }

    /// <summary>
    ///     Enter on a row: switch to its session, or do nothing beyond closing
    ///     the palette when the row is a worktree with no session — the same
    ///     contract <see cref="WorktreeJumpSeeder" /> documents for the empty
    ///     <see cref="WorktreeJumpEntry.SessionId" /> it writes.
    /// </summary>
    private static async Task SwitchAsync(IReplHost host, CommandItem selected, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(selected.Id))
        {
            await host.SwitchToSessionAsync(selected.Id, ct).ConfigureAwait(false);
        }

        host.Palette.Hide();
        host.WakeUp();
    }

    private static List<SessionSeed> Seeds(IReadOnlyList<Session> sessions)
    {
        var seeds = new List<SessionSeed>(sessions.Count);
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            seeds.Add(new SessionSeed(
                session.Id,
                session.Title,
                session.Directory,
                session.GitBranch,
                session.Status.ToString().ToLowerInvariant(),
                session.GitIsDirty,
                // The extension, not `Kind == Subagent`: it also classifies the
                // legacy parent-id + "task(" shape, so rows persisted before
                // `Session.Kind` existed are hidden here too.
                session.IsSubagent()));
        }

        return seeds;
    }

    /// <summary>
    ///     The repository's linked worktrees through the Domain port. A host
    ///     with no git query — or a repository git cannot answer — lists
    ///     sessions only, which is the documented degradation and the reason
    ///     this never forks <c>git</c> itself (#666).
    /// </summary>
    /// <remarks>
    ///     The <c>try</c>/<c>catch</c> is belt-and-braces, kept for the reason
    ///     the jump panel keeps its own: this runs on a keypress path, and an
    ///     exception here would take the input loop down rather than leave a
    ///     thinner list.
    /// </remarks>
    private static IReadOnlyList<GitWorktreeInfo> Worktrees(IReplHost host)
    {
        if (host.Git is not { } git)
        {
            return [];
        }

        try
        {
            return git.ListWorktrees(Environment.CurrentDirectory);
        }
        catch
        {
            return [];
        }
    }
}
