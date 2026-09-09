namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Palette slash reference — opens the full slash-command palette.</summary>
internal sealed class HelpCommand : IReplCommand
{
    public string Id => "help";
    public IReadOnlyList<string> Aliases => ["h"];
    public string Title => "Help";
    public string Description => "slash commands reference";
    public string Group => "General";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Host.OpenSlashPalette();
        ctx.Host.WakeUp();
        return Task.CompletedTask;
    }
}
