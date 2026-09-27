using Harbor.App.Cli.Hosting;
using Harbor.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor events --watch</c> — print the daemon's event stream as
///     JSON lines. Defaults to <c>HARBOR_MODE=ipc-client</c>.
///     Extracted from <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class EventsVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (!EventsWatchOptions.TryParse(args, out var options, out string? parseError) || options is null)
        {
            Console.Error.WriteLine(parseError);
            EventsWatchOptions.PrintUsage(Console.Out);
            return 2;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARBOR_MODE")))
            Environment.SetEnvironmentVariable("HARBOR_MODE", "ipc-client");

        using var host = HostBuilder.Build(args);
        var client = host.Services.GetRequiredService<IHarborClient>();
        return await EventsWatchRunner.RunAsync(Console.Out, Console.Error, client, options).ConfigureAwait(false);
    }
}
