using Harbor.Abstractions.Extensions;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
/// Setup entry (issue #1248, slice 1): <c>/setup</c> opens the interactive
/// onboarding flow (provider → key → model → agent) as a CellForge dialog overlay,
/// persisted to the config/auth stores. <c>/checklist</c> keeps the read-only
/// setup-guide progress surface (issue #383).
/// </summary>
internal sealed class SetupCommand : IReplCommand
{
    public string Id => "setup";
    public IReadOnlyList<string> Aliases => ["checklist"];
    public string Title => "Setup";
    public string Description => "interactive setup (provider, key, model, agent)";
    public string Group => "Config";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        Ensure.NotNull(ctx);
        ctx.Host.Palette.Hide();

        if (!string.Equals(ctx.RawId, "checklist", StringComparison.OrdinalIgnoreCase)
            && ctx.Host is Harbor.App.Cli.Repl.CellForgeReplRunner runner)
        {
            return runner.Onboarding.OpenAsync(ct);
        }

        // The checklist overlay is seated on the chat screen by every CellForge
        // host; Show() paints the last published snapshot, so the modal opens
        // with the current progress rather than an empty list.
        ctx.Host.Screen.SetupChecklist.Show();
        ctx.Host.WakeUp();
        return Task.CompletedTask;
    }
}
