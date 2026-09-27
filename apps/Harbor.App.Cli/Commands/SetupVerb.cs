using Harbor.App.Cli.Hosting;
using Harbor.Application.Onboarding;
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
        logger.LogInformation("Starting setup wizard");
        using var host = HostBuilder.Build();
        var wizard = host.Services.GetRequiredService<OnboardingWizard>();
        var renderer = host.Services.GetRequiredService<ITuiRenderer>();
        await renderer.InitializeAsync().ConfigureAwait(false);
        var writer = (Action<string>)(msg => _ = renderer.WriteLineAsync(msg));
        var reader = (Func<string, Task<string>>)(async prompt =>
        {
            var r = await renderer.ReadLineAsync(prompt).ConfigureAwait(false);
            return r.IsSuccess ? r.Value : string.Empty;
        });
        var result = await wizard.RunAsync(reader, writer).ConfigureAwait(false);
        logger.LogInformation("Setup wizard finished with success={Success}", result.IsSuccess);
        return result.IsSuccess ? 0 : 1;
    }
}
