using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the pre-turn model option check (#2686 Q07).</summary>
internal static class CafProviderOptionCommands
{
    /// <summary>Validate options for a model before a turn: models preflight &lt;provider&gt; &lt;model&gt; [--option name=value].</summary>
    public static Task<int> Preflight([Argument] params string[] args) =>
        ProviderOptionRunner.RunAsync(args, CancellationToken.None);
}
