namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Read-only info commands (providers/plugins/permissions) via the slash dispatcher.</summary>
internal sealed class InfoCommand : IReplCommand
{
    public string Id => "providers";
    public IReadOnlyList<string> Aliases => ["plugins", "permissions"];
    public string Title => "Providers";
    public string Description => "list configured providers";
    public string Group => "Other";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        string text = ctx.RawInput ?? '/' + ctx.RawId;
        return ctx.Host.ExecuteInfoAsync(text, ct);
    }
}
