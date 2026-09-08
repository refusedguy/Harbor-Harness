using Harbor.Abstractions.Sessions;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Sessions menu: switch / branch-tree / new session.</summary>
internal sealed class SessionsCommand : IReplCommand
{
    public string Id => "sessions";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Sessions";
    public string Description => "list stored sessions";
    public string Group => "Sessions";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;
        if (host.Services.GetService<ISessionStore>() is null)
        {
            host.Bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            host.WakeUp();
            return Task.CompletedTask;
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Sessions", "sessions",
            new List<CommandItem>
            {
                new("switch", "Switch Session", "Browse and switch to recent chat session", string.Empty, "Actions"),
                new("tree", "Branch Tree", "Show session fork / lineage tree", string.Empty, "Actions"),
                new("new", "New Session", "Start a fresh chat session", string.Empty, "Actions")
            },
            OnCommitAsync: (item, frameCt) => HandleMenuAsync(host, item, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private static async Task HandleMenuAsync(IReplHost host, CommandItem item, CancellationToken ct)
    {
        if (item.Id == "switch")
        {
            await ShowSwitchListAsync(host, ct).ConfigureAwait(false);
            return;
        }

        if (item.Id == "tree")
        {
            await new SessionTreeCommand().ExecuteAsync(new ReplCommandContext(host, "tree"), ct).ConfigureAwait(false);
            return;
        }

        if (item.Id == "new")
        {
            await new NewSessionCommand().ExecuteAsync(new ReplCommandContext(host, "new"), ct).ConfigureAwait(false);
            host.Palette.Hide();
            host.WakeUp();
        }
    }

    private static async Task ShowSwitchListAsync(IReplHost host, CancellationToken ct)
    {
        var store = host.Services.GetService<ISessionStore>();
        if (store is null)
        {
            host.Bridge.AppendSystemLine("⇄ переключение недоступно: хост без хранилища сессий");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {result.Error}");
            host.Palette.Hide();
            host.WakeUp();
            return;
        }

        var sessionItems = result.Value
            .Select(s => new CommandItem(
                s.Id,
                s.Title,
                $"{s.ProviderId}/{s.Model} · {s.Id[..Math.Min(8, s.Id.Length)]}",
                string.Empty,
                "Sessions"))
            .ToList();

        host.Palette.PushFrame(new PaletteFrame(
            "Switch Session", "sessions / switch", sessionItems,
            OnCommitAsync: (sessionItem, frameCt) => SwitchAsync(host, sessionItem, frameCt)));
        host.WakeUp();
    }

    private static async Task SwitchAsync(IReplHost host, CommandItem sessionItem, CancellationToken ct)
    {
        await host.SwitchToSessionAsync(sessionItem.Id, ct).ConfigureAwait(false);
        host.Palette.Hide();
        host.WakeUp();
    }
}
