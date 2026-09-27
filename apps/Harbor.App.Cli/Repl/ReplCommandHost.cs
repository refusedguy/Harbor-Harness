using System.Linq;
using System.Text;
using Harbor.App.Cli.Repl.Commands;
using Harbor.Tui.CellForge.Widgets;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Command surface behind the CellForge REPL (G2 split of
///     <see cref="CellForgeReplRunner"/>, issue #174): the command palette,
///     slash-command palette, vim-mode toggle, leader-chord bindings and the
///     palette/info execution paths. Stateless service — all mutable state
///     stays on the runner and is reached through its internal accessors, so
///     the split moves code without moving behavior.
/// </summary>
internal sealed class ReplCommandHost(CellForgeReplRunner host)
{
    private SlashCommandDispatcher? _dispatcher;

    private SlashCommandDispatcher GetDispatcher()
    {
        _dispatcher ??= host.LegacySlash.Dispatcher;
        return _dispatcher;
    }

    // ── Command palette ────────────────────────────────────────────────────

    /// <summary>Suggested commands, generated from the catalog so the palette
    /// never drifts from the registered set. Exit stays explicit (dispatcher).</summary>
    internal void OpenCommandPalette()
    {
        var items = host._catalog.GetAll()
            .Select(c => new CommandItem(c.Id, c.Title, c.Description, string.Empty, c.Group))
            .ToList();
        items.Add(new CommandItem("exit", "Exit", "quit harbor", "ctrl+c ×2", "General"));
        host._palette.PushFrame(new PaletteFrame(
            "Commands",
            "",
            items,
            OnCommitAsync: (item, frameCt) => ExecutePaletteItemAsync(item, frameCt)));
    }

    /// <summary>Opens the palette pre-populated with every registered slash command.</summary>
    internal void OpenSlashPalette()
    {
        var commands = GetDispatcher().GetRegisteredCommands();
        var items = commands.Select(cmd => new CommandItem(
            Id: cmd.Name,
            Title: cmd.Name,
            Detail: cmd.Description,
            Shortcut: cmd.Usage,
            Group: cmd.Name is "help" or "exit" or "quit" ? "General"
                : cmd.Name is "setup" or "auth" ? "Config"
                : cmd.Name is "model" or "agent" or "tui" or "renderer" or "storage" ? "Runtime"
                : "Other"
        )).ToArray();
        host._palette.PushFrame(new PaletteFrame(
            "Commands",
            "slash",
            items,
            OnCommitAsync: (item, frameCt) => ExecutePaletteItemAsync(item, frameCt)));
    }

    internal void ToggleVimMode()
    {
        if (host._vim.Enabled)
        {
            host._vim.Enabled = false;
            host._vim.Reset();
        }
        else
        {
            host._vim.Enabled = true;
        }

        host.Bridge.AppendSystemLine(host._vim.Enabled
            ? "vim: on — Esc = normal, i/a/A/I = insert"
            : "vim: off");
    }

    /// <summary>Sessions already checked for auto-titling (one check per session lifetime).</summary>
    /// <summary>Leader-chord bindings: scroll anchors, palette, vim, slash
    /// shortcuts, and quick-switch digits 1..9 (recent sessions, sprint UI-V2 P2.2).
    /// Scroll anchors are msg-bound (epic C): resolving them stages a store
    /// KeyInput the frame loop dispatches — the reducer owns the meaning.</summary>
    internal void BindLeaderKeys()
    {
        host._leader.Bind('g', VirtualizedChatTimeline.ScrollTopMsg(),
            () => { host._timeline.ScrollToTop(); host._wake.Writer.TryWrite(null); });
        host._leader.Bind('e', VirtualizedChatTimeline.ScrollBottomMsg(),
            () => { host._timeline.ScrollToEnd(Math.Max(1, host._timelineViewportH)); host._wake.Writer.TryWrite(null); });
        host._leader.Bind('p', () => { OpenCommandPalette(); host._wake.Writer.TryWrite(null); });
        host._leader.Bind('v', () => { ToggleVimMode(); host._wake.Writer.TryWrite(null); });
        host._leader.Bind('h', () => host._leaderSlash = "help");
        host._leader.Bind('s', () => host._leaderSlash = "sessions");
        host._leader.Bind('m', () => host._leaderSlash = "model");
        host._leader.Bind('a', () => host._leaderSlash = "agent");
        foreach (char d in "123456789")
        {
            host._leader.Bind(d, () => host._quickSwitchChord = d);
        }
    }

    /// <summary>
    ///     Quick-switch slot resolution (<c>&lt;leader&gt;1..9</c>, sprint UI-V2
    ///     P2.2): loads the bound session from the store and rebinds the idle
    ///     agent to it. The timeline shows only new traffic from the switch on.
    /// </summary>
    internal async Task ExecuteInfoCommandAsync(string text, CancellationToken ct)
    {
        var captured = new List<string>();
        var writer = new Action<string>(s => captured.Add(s));

        try
        {
            var outcome = await host.LegacySlash.RunAsync(
                text,
                writer,
                prompt =>
                {
                    captured.Add($"{prompt} — interactive input unavailable in consoleex");
                    return Task.FromResult(string.Empty);
                },
                host.Agent, host.SessionModel).ConfigureAwait(false);

            if (outcome.ShouldQuit)
            {
                host._slashExitCode = outcome.ExitCode;
                host._quitRequested = true;
            }
        }
        catch (Exception ex)
        {
            captured.Add($"Error: {ex.Message}");
        }

        if (captured.Count == 0)
        {
            host._wake.Writer.TryWrite(null);
            return;
        }

        string cmd = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        string formatted = FormatInfoBlock(cmd, captured);
        host.Bridge.AppendSystemLine(formatted);
        host._wake.Writer.TryWrite(null);
    }

    private static string FormatInfoBlock(string title, IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append("┌─ ").Append(title).Append(' ');
        int pad = Math.Max(0, 56 - title.Length - 4);
        sb.Append('─', pad);
        sb.AppendLine("┐");
        foreach (var line in lines)
        {
            sb.Append("│ ").Append(line);
            int trail = Math.Max(0, 56 - line.Length - 2);
            if (trail > 0) sb.Append(' ', trail);
            sb.AppendLine(" │");
        }
        sb.Append("└").Append('─', 56).AppendLine("┘");
        return sb.ToString();
    }

    internal async Task ExecutePaletteItemAsync(CommandItem item, CancellationToken ct)
    {
        // GoF Command: single catalog lookup; uncatalogued ids fall through
        // to the slash-dispatcher fallback below. New command = new file +
        // Register, no switch edits (OCP).
        if (host._catalog.TryResolve(item.Id, out var cmd) && cmd is not null)
        {
            await cmd.ExecuteAsync(new ReplCommandContext(host, item.Id), ct).ConfigureAwait(false);
            return;
        }

        string slash = '/' + item.Id;
        try
        {
            await host.LegacySlash.RunAsync(
                slash,
                line => { host.Bridge.AppendSystemLine(line); host._wake.Writer.TryWrite(null); },
                prompt =>
                {
                    host.Bridge.AppendSystemLine($"{prompt} — интерактивный ввод недоступен в consoleex, используйте legacy TUI (/exit)");
                    host._wake.Writer.TryWrite(null);
                    return Task.FromResult(string.Empty);
                },
                host.Agent, host.SessionModel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            host.Log.LogError(ex, "Palette command /{Id} failed", item.Id);
            host.Bridge.AppendSystemLine($"! {ex.Message}");
            host._wake.Writer.TryWrite(null);
        }
    }
}
