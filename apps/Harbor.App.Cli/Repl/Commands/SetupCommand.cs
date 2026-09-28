namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
/// Setup checklist re-entry (issue #383) — the post-wizard "you are N/M done,
/// here is what is left" surface. Complements the linear onboarding wizard,
/// which stays a separate, optional flow reachable through <c>/config</c>,
/// <c>/model</c> and <c>/auth</c>.
/// </summary>
internal sealed class SetupCommand : IReplCommand
{
    public string Id => "setup";
    public IReadOnlyList<string> Aliases => ["checklist"];
    public string Title => "Setup";
    public string Description => "setup guide checklist (progress + remaining steps)";
    public string Group => "Config";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Host.Palette.Hide();

        // The checklist overlay is seated on the chat screen by every CellForge
        // host; Show() paints the last published snapshot, so the modal opens
        // with the current progress rather than an empty list.
        ctx.Host.Screen.SetupChecklist.Show();
        ctx.Host.WakeUp();
        return Task.CompletedTask;
    }
}
