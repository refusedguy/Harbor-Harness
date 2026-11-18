using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     #2669: CAF handler for the <c>completions</c> verb. It rides the one
///     builder in <see cref="CafPilotVerbs"/> — no second <c>RunAsync</c> site
///     (two sites collide with CS8785, see the pilot remarks).
/// </summary>
internal static class CafCompletionCommands
{
    /// <summary>Print a shell completion script for bash, zsh, fish or powershell.</summary>
    /// <param name="shell">Target shell: bash, zsh, fish or powershell.</param>
    public static int Completions([Argument] string shell) => CompletionVerbs.Run(shell);
}
