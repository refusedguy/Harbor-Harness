using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor serve</c> — foreground alias over <see cref="HeadlessVerb" />:
///     full agent host + IPC server, blocking until SIGINT/SIGTERM.
///     Extracted from <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class ServeVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args)
    {
        var options = ServeOptions.Parse(args);
        if (options.ShowHelp)
        {
            ServeOptions.PrintUsage(Console.Out);
            return 0;
        }

        return await HeadlessVerb.RunAsync(logger, args).ConfigureAwait(false);
    }
}
