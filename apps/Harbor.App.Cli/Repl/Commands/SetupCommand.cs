namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Setup hint — interactive wizard needs a direct console.</summary>
internal sealed class SetupCommand : IReplCommand
{
    public string Id => "setup";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Setup";
    public string Description => "setup wizard hint (direct console only)";
    public string Group => "Config";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Host.Bridge.AppendSystemLine("⚠ Setup wizard requires a direct console. Use '/config', '/model', '/auth' or run 'harbor setup' from terminal.");
        ctx.Host.Palette.Hide();
        ctx.Host.WakeUp();
        return Task.CompletedTask;
    }
}
