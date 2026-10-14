using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Hosting;
using Harbor.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor auth …</c> — API-key management via the REPL
///     <see cref="AuthCommand" /> slash command. Extracted from
///     <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class AuthVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args)
    {
        AuthVerbLog.StartingAuth(logger);
        using var host = HostBuilder.Build(args);
        var authStore = host.Services.GetRequiredService<AuthStore>();
        var writer = (Action<string>)Console.WriteLine;
        var cmd = new AuthCommand(authStore, writer);
        var ctx = new SimpleCommandContext(null!, null!,
            host.Services.GetRequiredService<IProviderRegistry>(),
            host.Services.GetRequiredService<IToolRegistry>(),
            writer, _ => Task.FromResult(string.Empty));
        // Propagate the command's own result (0 = success, non-zero = failure)
        // instead of unconditionally reporting success.
        var authResult = await cmd.ExecuteAsync(args, ctx).ConfigureAwait(false);
        return authResult.IsSuccess ? 0 : 1;
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="AuthVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AuthVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting auth command")]
    public static partial void StartingAuth(ILogger logger);
}
