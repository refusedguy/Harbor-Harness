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
        ConfigVerbLog.StartingConfig(logger);
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

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="ConfigVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ConfigVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting config command")]
    public static partial void StartingConfig(ILogger logger);
}
