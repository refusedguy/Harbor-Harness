using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Commands;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Session branch tree: forest view → per-node switch/fork actions.</summary>
internal sealed class SessionTreeCommand : IReplCommand
{
    public string Id => "tree";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Branch Tree";
    public string Description => "show session fork / lineage tree";
    public string Group => "Sessions";

    public async Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        var store = host.Services.GetService<ISessionStore>();
        if (store is null)
        {
            host.Bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            host.WakeUp();
            return;
        }

        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {result.Error}");
            host.WakeUp();
            return;
        }

        var lines = SessionTreeRunner.RenderForest(result.Value, host.SessionModel.Id);
        var sessionsById = result.Value.ToDictionary(s => s.Id);

        var treeItems = new List<CommandItem>();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string sid = string.Empty;
            foreach (var s in result.Value)
            {
                if (line.Contains(s.Id, StringComparison.Ordinal))
                {
                    sid = s.Id;
                    break;
                }
            }

            treeItems.Add(string.IsNullOrEmpty(sid)
                ? new CommandItem($"info_{i}", line, string.Empty, string.Empty, "Session Tree")
                : new CommandItem(sid, line, "Select to inspect / switch / fork", string.Empty, "Session Tree"));
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Session Tree", "sessions / tree", treeItems,
            OnCommitAsync: (selected, frameCt) => ShowNodeActionsAsync(host, store, sessionsById, selected, frameCt)));
        host.WakeUp();
    }

    private static Task ShowNodeActionsAsync(
        IReplHost host,
        ISessionStore store,
        Dictionary<string, Harbor.Abstractions.Models.Session> sessionsById,
        CommandItem selected,
        CancellationToken ct)
    {
        if (selected.Id.StartsWith("info_") || !sessionsById.TryGetValue(selected.Id, out var targetSession))
        {
            return Task.CompletedTask;
        }

        host.Palette.PushFrame(new PaletteFrame(
            $"Session {targetSession.Id[..Math.Min(8, targetSession.Id.Length)]}",
            $"tree / {targetSession.Id[..Math.Min(8, targetSession.Id.Length)]}",
            new List<CommandItem>
            {
                new("switch", "Switch to this session", $"Activate session {targetSession.Id[..Math.Min(8, targetSession.Id.Length)]}", string.Empty, "Actions"),
                new("fork", "Fork new branch from this session", "Create a branch copying history", string.Empty, "Actions")
            },
            OnCommitAsync: (action, frameCt) => ApplyNodeActionAsync(host, store, targetSession.Id, action, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task ApplyNodeActionAsync(
        IReplHost host,
        ISessionStore store,
        string targetSessionId,
        CommandItem action,
        CancellationToken ct)
    {
        if (action.Id == "switch")
        {
            await host.SwitchToSessionAsync(targetSessionId, ct).ConfigureAwait(false);
        }
        else if (action.Id == "fork")
        {
            await ForkAsync(host, store, targetSessionId, ct).ConfigureAwait(false);
        }
        host.Palette.Hide();
        host.WakeUp();
    }

    private static async Task ForkAsync(IReplHost host, ISessionStore store, string targetSessionId, CancellationToken ct)
    {
        var messages = await store.GetMessagesAsync(targetSessionId, ct).ConfigureAwait(false);
        if (!messages.IsSuccess || messages.Value.Count == 0)
        {
            host.Bridge.AppendSystemLine("⚠ Cannot fork an empty session.");
            return;
        }

        var lastMsgId = messages.Value[^1].Id;
        var forked = await new SessionForkRunner(store).ForkAsync(targetSessionId, lastMsgId, ct).ConfigureAwait(false);
        if (forked.IsSuccess)
        {
            await host.SwitchToSessionAsync(forked.Value.ForkId, ct).ConfigureAwait(false);
            host.Bridge.AppendSystemLine($"✓ Forked branch {forked.Value.ForkId[..Math.Min(8, forked.Value.ForkId.Length)]} ({forked.Value.Copied} messages copied)");
        }
    }
}
