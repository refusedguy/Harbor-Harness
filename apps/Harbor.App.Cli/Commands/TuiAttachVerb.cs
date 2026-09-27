using Harbor.App.Cli.Hosting;
using Harbor.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor tui</c> — attach to a live daemon: handshake, optional
///     session bind, then the event stream as JSON lines until Ctrl-C.
///     Defaults to <c>HARBOR_MODE=ipc-client</c> so it attaches instead of
///     spawning its own agent. Extracted from <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class TuiAttachVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (!TuiAttachOptions.TryParse(args, out var options, out string? parseError) || options is null)
        {
            Console.Error.WriteLine(parseError);
            TuiAttachOptions.PrintUsage(Console.Out);
            return 2;
        }

        if (options.ShowHelp)
        {
            TuiAttachOptions.PrintUsage(Console.Out);
            HelpVerbs.PrintTuiOptions();
            return 0;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARBOR_MODE")))
            Environment.SetEnvironmentVariable("HARBOR_MODE", "ipc-client");

        using var host = HostBuilder.Build(args);
        var client = host.Services.GetRequiredService<IHarborClient>();
        return await TuiAttachRunner.RunAsync(Console.Out, Console.Error, client, options).ConfigureAwait(false);
    }
}
