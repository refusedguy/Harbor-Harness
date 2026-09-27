using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Hosting;
using Harbor.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor config …</c> — show/edit configuration via the REPL
///     <see cref="ConfigCommand" /> slash command. Extracted from
///     <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class ConfigVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args)
    {
        logger.LogInformation("Starting config command");
        using var host = HostBuilder.Build(args);
        var configStore = host.Services.GetRequiredService<IConfigStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new ConfigCommand(configStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<IProviderRegistry>(),
            host.Services.GetRequiredService<IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        // Propagate the command's own result (0 = success, non-zero = failure)
        // instead of unconditionally reporting success.
        var configResult = await cmd.ExecuteAsync(args, ctx).ConfigureAwait(false);
        return configResult.IsSuccess ? 0 : 1;
    }
}
