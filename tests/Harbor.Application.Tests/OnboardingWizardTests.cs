using CSharpFunctionalExtensions;
using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Tests;
/// <summary>
///     ROP-B П.15: OnboardingWizard.RunAsync as a Bind chain — each step
///     runs only after the previous succeeded, failures short-circuit with
///     their own message, and the config save failure passes through.
/// </summary>
public class OnboardingWizardTests
{
    private sealed class FakeConfigStore : IConfigStore
    {
        public HarborConfig? Saved;
        public Result? UpdateOutcome;
        public string? ApiKeyLookup;

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new HarborConfig()));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) =>
            Task.FromResult(UpdateOutcome ?? Result.Success());

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            if (UpdateOutcome is { IsFailure: true } failure)
                return Task.FromResult(failure);

            Saved = updater(new HarborConfig());
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(ApiKeyLookup is { } key
                ? Result.Success(key)
                : Result.Failure<string>("not set"));
    }

    private static Queue<string> Inputs(params string[] items) => new(items);

    private static Func<string, Task<string>> Reader(Queue<string> inputs) =>
        _ => Task.FromResult(inputs.Count > 0 ? inputs.Dequeue() : "");

    [Test]
    public async Task RunAsync_NoAuthProvider_CompletesAndSavesConfig()
    {
        var store = new FakeConfigStore();
        var wizard = new OnboardingWizard(store, new AuthStore(store), NullLoggerFactory.Instance.CreateLogger<OnboardingWizard>());
        var output = new List<string>();

        var result = await wizard.RunAsync(
            Reader(Inputs("ollama", "", "1")), output.Add);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(store.Saved).IsNotNull();
        await Assert.That(store.Saved!.Onboarded).IsTrue();
        await Assert.That(output.Any(l => l.Contains("Setup complete"))).IsTrue();
    }

    [Test]
    public async Task RunAsync_MissingApiKey_FailsWithoutSaving()
    {
        var store = new FakeConfigStore();
        var wizard = new OnboardingWizard(store, new AuthStore(store), NullLoggerFactory.Instance.CreateLogger<OnboardingWizard>());
        var output = new List<string>();
        var preset = ProviderPresets.All.First(p => p.RequiresApiKey);
        int idx = -1;
        for (int i = 0; i < ProviderPresets.All.Count; i++)
        {
            if (ProviderPresets.All[i].Id == preset.Id) { idx = i + 1; break; }
        }

        var result = await wizard.RunAsync(
            Reader(Inputs(idx.ToString(), "")), output.Add);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("No API key provided.");
        await Assert.That(store.Saved).IsNull();
    }

    [Test]
    public async Task RunAsync_SaveFails_FailurePassesThrough()
    {
        var store = new FakeConfigStore { UpdateOutcome = Result.Failure("disk full") };
        var wizard = new OnboardingWizard(store, new AuthStore(store), NullLoggerFactory.Instance.CreateLogger<OnboardingWizard>());
        var output = new List<string>();

        var result = await wizard.RunAsync(
            Reader(Inputs("ollama", "", "1")), output.Add);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("disk full");
        await Assert.That(output.Any(l => l.Contains("Setup complete"))).IsFalse();
    }

    [Test]
    public async Task RunAsync_InvalidThenValidProvider_LoopsUntilValid()
    {
        var store = new FakeConfigStore();
        var wizard = new OnboardingWizard(store, new AuthStore(store), NullLoggerFactory.Instance.CreateLogger<OnboardingWizard>());
        var output = new List<string>();

        var result = await wizard.RunAsync(
            Reader(Inputs("bogus", "ollama", "", "1")), output.Add);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(output.Any(l => l.Contains("Invalid selection: bogus"))).IsTrue();
    }
}
