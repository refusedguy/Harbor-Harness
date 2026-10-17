using CSharpFunctionalExtensions;
using Harbor.App.Cli.Hosting;
using Harbor.Application.Configuration;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor setup</c> — run the onboarding wizard. Extracted from
///     <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class SetupVerb
{
    internal static async Task<int> RunAsync(ILogger logger)
    {
        SetupVerbLog.StartingSetup(logger);
        using var host = HostBuilder.Build();
        var wizard = host.Services.GetRequiredService<OnboardingWizard>();
        var renderer = host.Services.GetRequiredService<ITuiRenderer>();
        await renderer.InitializeAsync().ConfigureAwait(false);
        var writer = (Action<string>)(msg => _ = renderer.WriteLineAsync(msg));
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return r.GetValueOrDefault(string.Empty);
        });
        var result = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
        SetupVerbLog.SetupFinished(logger, result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="SetupVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SetupVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting setup wizard")]
    public static partial void StartingSetup(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Setup wizard finished with success={Success}")]
    public static partial void SetupFinished(ILogger logger, bool success);
}
