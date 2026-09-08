namespace Harbor.App.Cli.Repl.Commands;

/// <summary>Context passed to every command: host primitives + raw id.</summary>
internal sealed record ReplCommandContext(IReplHost Host, string RawId);
