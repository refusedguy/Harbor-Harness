using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tui;
using Harbor.Application.Onboarding;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     /setup — run onboarding wizard.
/// </summary>
public sealed class SetupCommand(OnboardingWizard wizard, Func<string, Task<string>> reader, Action<string> writer) : ISlashCommand
{
    public string Name => "setup";
    public string Description => "Run setup wizard (provider, API key, model)";
    public string Usage => "/setup";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public IReadOnlyList<string>? ArgSuggestions => null;

    public async Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
    {
        var result = await wizard.RunAsync(reader, writer, ct).ConfigureAwait(false);
        return result;
    }
}
