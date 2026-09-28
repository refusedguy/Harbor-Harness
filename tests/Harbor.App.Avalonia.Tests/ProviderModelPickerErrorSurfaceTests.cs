using CommunityToolkit.Mvvm.Messaging;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #484 — the model picker used to swallow a failed
///     <c>GetAllModelsAsync</c>: it only logged, so a provider that could not
///     be reached looked exactly like a provider that published no models.
///     These tests pin the error surface of both provider surfaces and the
///     distinction between "failed" and "no models".
/// </summary>
public class ProviderModelPickerErrorSurfaceTests
{
    [Test]
    public async Task PickerLoad_RegistryFails_ShowsErrorMessage()
    {
        var vm = CreatePicker(FailingRegistry());

        await vm.LoadCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLoading).IsFalse();
        await Assert.That(vm.ErrorMessage).IsNotEmpty();
        await Assert.That(vm.ErrorMessage).Contains("connection refused");
        await Assert.That(vm.AllProviders).IsEmpty();
    }

    [Test]
    public async Task PickerLoad_Succeeds_ClearsPreviousErrorAndPopulatesRows()
    {
        var vm = CreatePicker(ServingRegistry("gpt-5"));

        await vm.LoadCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLoading).IsFalse();
        await Assert.That(vm.ErrorMessage).IsEmpty();
        await Assert.That(vm.AllProviders.Count).IsEqualTo(1);
        await Assert.That(vm.FilteredProviders.Count).IsEqualTo(1);
        await Assert.That(vm.AllProviders[0].Models.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PickerLoad_RetryAfterFailure_ClearsTheErrorBanner()
    {
        FakeRegistry registry = FailingRegistry();
        var vm = CreatePicker(registry);

        await vm.LoadCommand.ExecuteAsync(null);
        await Assert.That(vm.ErrorMessage).IsNotEmpty();

        registry.Models = Result.Success<IReadOnlyList<ModelInfo>>([Model("gpt-5")]);
        await vm.LoadCommand.ExecuteAsync(null);

        await Assert.That(vm.ErrorMessage).IsEmpty();
        await Assert.That(vm.FilteredProviders.Count).IsEqualTo(1);
    }

    [Test]
    public async Task PickerLoad_SucceedsWithNoModels_SaysNoModels_NotAFailure()
    {
        var vm = CreatePicker(ServingRegistry());

        await vm.LoadCommand.ExecuteAsync(null);

        await Assert.That(vm.ErrorMessage).Contains("No models returned");
        await Assert.That(vm.ErrorMessage).DoesNotContain("connection refused");
    }

    [Test]
    public async Task BrowserLoad_RegistryFails_ShowsErrorMessage()
    {
        var vm = new ProviderBrowserViewModel(
            FailingRegistry(), NullLogger<ProviderBrowserViewModel>.Instance);

        await vm.LoadProvidersCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLoading).IsFalse();
        await Assert.That(vm.ErrorMessage).Contains("connection refused");
    }

    [Test]
    public async Task BrowserLoad_SucceedsWithNoModels_NamesTheProvider()
    {
        var vm = new ProviderBrowserViewModel(
            ServingRegistry(), NullLogger<ProviderBrowserViewModel>.Instance);

        await vm.LoadProvidersCommand.ExecuteAsync(null);

        await Assert.That(vm.ErrorMessage).Contains("No models returned by provider 'ollama'");
    }

    private static ProviderModelPickerViewModel CreatePicker(IProviderRegistry registry) =>
        new(registry,
            new StubConfigStore(),
            new StubSessionManager(),
            new StubToastService(),
            NullLogger<ProviderModelPickerViewModel>.Instance,
            WeakReferenceMessenger.Default);

    private static FakeRegistry FailingRegistry() =>
        new() { Models = Result.Failure<IReadOnlyList<ModelInfo>>("connection refused") };

    private static FakeRegistry ServingRegistry(params string[] modelIds)
    {
        ModelInfo[] models = modelIds.Select(Model).ToArray();
        return new FakeRegistry { Models = Result.Success<IReadOnlyList<ModelInfo>>(models) };
    }

    private static ModelInfo Model(string modelId) =>
        new(modelId, "ollama", modelId, 128_000, 8_192,
            false, false, true, Pricing.Unknown, "openai");

    /// <summary>Registry with one provider and a caller-controlled model catalog.</summary>
    private sealed class FakeRegistry : IProviderRegistry
    {
        public Result<IReadOnlyList<ModelInfo>> Models { get; set; } =
            Result.Success<IReadOnlyList<ModelInfo>>([]);

        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [ProviderId.Create("ollama")];

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            Result.Failure<ILlmClient>("not registered");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Models);

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            GetAllModelsAsync(cancellationToken);

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) => Result.Failure("not supported");
    }

    private sealed class StubConfigStore : ICommonConfigStore
    {
        public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new CommonConfig()));

        public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class StubToastService : IToastService
    {
#pragma warning disable CS0067
        public event EventHandler<ToastNotification>? ToastAdded;
#pragma warning restore CS0067

        public void Show(string message, ToastKind kind = ToastKind.Info) { }
    }

    private sealed class StubSessionManager : ISessionManager
    {
        public Session? Active => null;
        public SessionContext? ActiveContext => null;
        public SessionContext? GetContext(string sessionId) => null;
        public GitSessionInfo GetGitInfo(string sessionId) => new(null, false, 0, null);
        public void RefreshGitInfo(string sessionId, string directory) { }
        public Task EnsureDefaultSessionAsync() => Task.CompletedTask;
        public Task RebindFromCommonConfigAsync() => Task.CompletedTask;

        public Task<Result<Session>> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null, string? workingDirectory = null) =>
            Task.FromResult(Result.Failure<Session>("not configured"));

        public Task<bool> OpenSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<Result<Session>> BranchActiveAsync() => Task.FromResult(Result.Failure<Session>("not configured"));
        public Task<bool> DeleteSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<bool> RenameSessionAsync(string sessionId, string newTitle) => Task.FromResult(true);
        public SessionStatus GetStatus(string sessionId) => SessionStatus.Idle;
        public void SetStatus(string sessionId, SessionStatus status) { }
        public void NotifyMessageCount(string sessionId, int count) { }

#pragma warning disable CS0067
        public event Action<string, SessionStatus>? StatusChanged;
        public event Action<string, int>? MessageCountChanged;
#pragma warning restore CS0067
    }
}
