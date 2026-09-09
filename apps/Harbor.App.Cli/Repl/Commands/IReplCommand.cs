namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     Single REPL command (GoF Command): slash text + palette entry resolve
///     to one object. OCP: new command = new file + register, no switch edits.
///     AOT-friendly: no reflection, plain Task (I/O-bound, ValueTask buys nothing).
/// </summary>
internal interface IReplCommand
{
    string Id { get; }
    IReadOnlyList<string> Aliases { get; }
    string Title { get; }
    string Description { get; }
    string Group { get; }

    Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct);
}
