namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Local (non-slash) command: toggles the vim editing layer.</summary>
internal sealed class VimCommand : IReplCommand
{
    public string Id => "vim";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Toggle vim mode";
    public string Description => "normal/insert editing layer";
    public string Group => "Local";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Host.ToggleVimMode();
        ctx.Host.WakeUp();
        return Task.CompletedTask;
    }
}
