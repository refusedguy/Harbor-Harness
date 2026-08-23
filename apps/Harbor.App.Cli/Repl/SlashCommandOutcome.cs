namespace Harbor.Cli.Repl;

/// <summary>
///     Outcome of dispatching one slash command. Regular commands continue the
///     REPL; quit commands (<c>/exit</c>, <c>/quit</c>) carry the requested
///     process exit code so the caller can shut down through its normal cleanup
///     path (IPC stop, host dispose) instead of killing the process mid-flight.
/// </summary>
internal readonly record struct SlashCommandOutcome(bool ShouldQuit, int ExitCode)
{
    /// <summary>Keep the REPL running.</summary>
    internal static readonly SlashCommandOutcome Continue = default;

    /// <summary>Terminate the REPL with the supplied exit code.</summary>
    internal static SlashCommandOutcome Quit(int exitCode = 0) => new(true, exitCode);
}
