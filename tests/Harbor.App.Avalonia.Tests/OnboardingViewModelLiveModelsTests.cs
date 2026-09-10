using System.Collections.ObjectModel;
using CSharpFunctionalExtensions;
using CommunityToolkit.Mvvm.Messaging;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Tests;
/// <summary>
///     PROD-UI-0 З.4 — desktop onboarding VM fetches a live model list for the
///     selected provider and degrades explicitly to free-text when unavailable.
///     Pure MVVM: no Avalonia UI involved.
/// </summary>
public class OnboardingViewModelLiveModelsTests
{
    private static OnboardingViewModel CreateVm(IProviderRegistry? registry)
    {
        return new OnboardingViewModel(
            new StubConfigStore(),
            new StubThemeService(),
            new StubToastService(),
            NullLogger<OnboardingViewModel>.Instance,
            WeakReferenceMessenger.Default,
            providers: registry);
    }

    [Test]
    public async Task LoadModels_Success_PopulatesList_AndSwitchesToPicker()
    {
        var vm = CreateVm(new FakeRegistry(["zzz", "aaa"]));

        // Select a provider first (ollama is default-selected in ctor).
        await vm.LoadModelsCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLiveModelList).IsTrue();
        await Assert.That(vm.AvailableModels.Count).IsEqualTo(2);
        await Assert.That(vm.AvailableModels[0]).IsEqualTo("aaa"); // sorted
        await Assert.That(vm.ModelListNote).IsEmpty();
    }

    [Test]
    public async Task LoadModels_UnreachableProvider_DegradesToFreeText()
    {
        var vm = CreateVm(new FakeRegistry(null));

        await vm.LoadModelsCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLiveModelList).IsFalse();
        await Assert.That(vm.AvailableModels.Count).IsEqualTo(0);
        await Assert.That(vm.ModelListNote).Contains("unavailable");
    }

    [Test]
    public async Task LoadModels_WithoutRegistry_KeepsFreeTextSilently()
    {
        var vm = CreateVm(null);

        await vm.LoadModelsCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLiveModelList).IsFalse();
        await Assert.That(vm.HasConnectionTest).IsFalse();
    }

    [Test]
    public async Task LoadModels_CurrentDefaultNotInList_SelectsFirst()
    {
        var vm = CreateVm(new FakeRegistry(["only-one"]));

        await vm.LoadModelsCommand.ExecuteAsync(null);

        await Assert.That(vm.IsLiveModelList).IsTrue();
        await Assert.That(vm.DefaultModel).IsEqualTo("only-one");
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

    private sealed class StubThemeService : IThemeService
    {
        public string Current => "dark";
        public bool IsDark => true;
        public void Apply(string theme) { }
        public void ApplyDark() { }
        public void ApplyLight() { }
        public void Toggle() { }
        public void ApplyHds(string theme) { }
        public void SetThemeVariant(bool isDark) { }
        public event EventHandler<string>? ThemeJsonApplied;
        public CSharpFunctionalExtensions.Result<string> LoadJson(string path) => CSharpFunctionalExtensions.Result.Success<string>(string.Empty);
        public CSharpFunctionalExtensions.Result ApplyJson(string json) => CSharpFunctionalExtensions.Result.Success();
        public System.IDisposable Watch(string path) => new NoopDisposable();
        private sealed class NoopDisposable : System.IDisposable { public void Dispose() { } }
    }

    private sealed class StubToastService : IToastService
    {
#pragma warning disable CS0067
        public event EventHandler<ToastNotification>? ToastAdded;
#pragma warning restore CS0067

        public void Show(string message, ToastKind kind = ToastKind.Info) { }
    }

    /// <summary>Registry serving one canned catalog (null → unregistered).</summary>
    private sealed class FakeRegistry(string[]? modelIds) : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() =>
            modelIds is null ? [] : [ProviderId.Create("fake")];

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            modelIds is null
                ? Result.Failure<ILlmClient>("not registered")
                : new FakeClient(providerId, modelIds);

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            GetAllModelsAsync(cancellationToken);

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) => Result.Failure("n/a");

        private sealed class FakeClient(ProviderId providerId, string[] modelIds) : ILlmClient
        {
            public ProviderId ProviderId { get; } = providerId;

            public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken = default)
            {
                async IAsyncEnumerable<LlmEvent> Empty()
                {
                    await Task.CompletedTask;
                    yield break;
                }
                return Empty();
            }

            public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(
                    modelIds.Select(id => new ModelInfo(
                        id, ProviderId.Value, id, 8192, 4096, false, false, false,
                        Pricing.Unknown, "openai")).ToList()));
        }
    }
}
