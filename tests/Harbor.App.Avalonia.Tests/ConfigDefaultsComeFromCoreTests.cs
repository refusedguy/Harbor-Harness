// ConfigDefaultsComeFromCoreTests.cs — behaviour pin for issue #677.
//
// WHAT THIS FILE PROVES, AND WHY IT IS NOT THE SAME CLAIM AS THE SOURCE GUARD
// ---------------------------------------------------------------------------
// tests/Harbor.Architecture.Tests/UiConfigDefaultsRule.cs proves the ABSENCE of
// a shape: no view-model line assigns a literal to a config default, none calls
// Environment.SetEnvironmentVariable. Absence of a shape is weak on its own —
// delete the whole field and the rule is still green while the user has lost
// the setting. These tests assert the BEHAVIOUR that replaced the shape, so a
// "fix" that empties the fields without reading the config cannot pass:
//
//   1. an unset config is shown as unset, not as a value the UI invented;
//   2. an explicit config value reaches the screen unchanged (read path);
//   3. whatever is on screen is what lands in the file, including "" (write
//      path — this is the one the bug lived in, because the wizard's write is
//      what made the field impossible to unset);
//   4. Save writes nothing to the process environment;
//   5. the onboarding wizard, which has no storage step, writes no backend.
//
// WHY "UNSET" IS THE ASSERTION, NOT A DETAIL
// -------------------------------------------
// CommonConfig.StorageBackend = "" is documented as "not chosen; the composition
// preset decides" (ADR-008: CLI jsonl, desktop memory — AppHost.cs:99 sets
// "memory"). The old view-model turned that "" into "jsonl" on load AND on
// save, so a desktop install could never reach its own preset, and reopening
// the wizard wrote "jsonl" straight back. Every assertion below is therefore
// about "" surviving the round trip, not about any particular backend name.
//
// The preset itself is already pinned end-to-end where it belongs —
// tests/Harbor.Hosting.Tests/RegistrationCompositionTests.
// AddHarbor_MemoryPreset_ResolvesMemorySessionStore composes with the same
// empty CommonConfig and asserts MemorySessionStore. These tests close the
// other half: nothing in the UI is allowed to fill that empty in on the way.

using Avalonia.Headless;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.Configuration;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Desktop.Abstractions.ViewModels;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Issue #677: a view-model renders the config it was handed and writes back
///     exactly that — it chooses no default and mutates no process state.
/// </summary>
[NotInParallel("avalonia-headless")]
public class ConfigDefaultsComeFromCoreTests
{
    /// <summary>Process-wide env names the old Save path wrote. #677 removed the writes.</summary>
    private static readonly string[] ProcessEnvNames = ["HARBOR_MODEL", "HARBOR_STORAGE", "HARBOR_LOGLEVEL", "OLLAMA_HOST"];

    /// <summary>
    ///     A config where the user has chosen NOTHING: every field the settings
    ///     screen touches is empty, which is what a fresh install and a
    ///     deliberately-unset field both look like.
    /// </summary>
    private static CommonConfig UnsetCommon() => new()
    {
        DefaultProvider = "",
        DefaultModel = "",
        StorageBackend = "",
        LogLevel = "",
        Theme = "",
        ConfigDirectory = Path.GetTempPath(),
    };

    private static AvaloniaConfig UnsetApp() => new() { FontFamily = "", Theme = "" };

    // ── read path ─────────────────────────────────────────────────────────

    [Test]
    public async Task UnsetConfig_IsShownUnset_NotAsAValueTheUiInvented()
    {
        await InAvaloniaSessionAsync(async vm =>
        {
            await Assert.That(vm.DefaultProvider).IsEmpty()
                .Because(
                    "CommonConfig.DefaultProvider is \"\" here. The UI filled it with \"ollama\" while the "
                    + "record's own default is \"anthropic\" — the UI was answering a question the record had "
                    + "already been asked. An empty box is the honest rendering of \"not chosen\".");
            await Assert.That(vm.DefaultModel).IsEmpty();
            await Assert.That(vm.StorageBackend).IsEmpty()
                .Because(
                    "StorageBackend = \"\" means the composition preset decides (ADR-008: CLI jsonl, desktop "
                    + "memory). The UI used to substitute \"jsonl\", which is the value the DESKTOP preset exists "
                    + "to override — the substitution made AppHost.cs:99 unreachable.");
            await Assert.That(vm.LogLevel).IsEmpty();
            await Assert.That(vm.FontFamily).IsEmpty();
            await Assert.That(vm.ThemeSettings.Theme).IsEmpty()
                .Because(
                    "Same substitution, same file: the constructor used to turn an empty CommonConfig.Theme into "
                    + "\"system\". Not in the source guard's field list (see its KNOWN GAP), but the principle is "
                    + "the same one — the record declares the default, the view renders what is stored.");
        }, new RecordingCommonStore(UnsetCommon()), new RecordingAppStore(UnsetApp()));
    }

    [Test]
    public async Task ExplicitConfigValues_ReachTheScreenUnchanged()
    {
        CommonConfig common = UnsetCommon() with
        {
            DefaultProvider = "groq",
            DefaultModel = "some/model",
            StorageBackend = "sqlite",
            LogLevel = "debug",
        };
        AvaloniaConfig app = UnsetApp() with { FontFamily = "Cascadia" };

        await InAvaloniaSessionAsync(async vm =>
        {
            await Assert.That(vm.DefaultProvider).IsEqualTo("groq");
            await Assert.That(vm.DefaultModel).IsEqualTo("some/model");
            await Assert.That(vm.StorageBackend).IsEqualTo("sqlite");
            await Assert.That(vm.LogLevel).IsEqualTo("debug");
            await Assert.That(vm.FontFamily).IsEqualTo("Cascadia");
        }, new RecordingCommonStore(common), new RecordingAppStore(app));
    }

    // ── write path ────────────────────────────────────────────────────────

    [Test]
    public async Task Save_WritesUnsetBackAsUnset_SoTheCompositionPresetStillDecides()
    {
        var commonStore = new RecordingCommonStore(UnsetCommon());
        var appStore = new RecordingAppStore(UnsetApp());

        await InAvaloniaSessionAsync(async vm =>
        {
            await vm.SaveCommand.ExecuteAsync(null);

            CommonConfig saved = commonStore.Saved
                ?? throw new InvalidOperationException("Save did not reach the common config store.");

            await Assert.That(saved.StorageBackend).IsEmpty()
                .Because(
                    "This is the bug. The screen said \"jsonl\", the wizard said \"jsonl\", and either one was enough "
                    + "to make the field non-empty forever — the user had no way to leave it unset and let the "
                    + "desktop preset (\"memory\") apply. A value written here is written for the NEXT launch, "
                    + "which is the launch the preset was supposed to govern.");
            await Assert.That(saved.DefaultProvider).IsEmpty();
            await Assert.That(saved.DefaultModel).IsEmpty();
            await Assert.That(saved.LogLevel).IsEmpty();
            await Assert.That(appStore.SavedApp!.FontFamily).IsEmpty();
        }, commonStore, appStore);
    }

    [Test]
    public async Task Save_WritesWhatTheUserChose_Verbatim()
    {
        var commonStore = new RecordingCommonStore(UnsetCommon());

        await InAvaloniaSessionAsync(async vm =>
        {
            vm.DefaultProvider = "mistral";
            vm.DefaultModel = "mistral-large";
            vm.StorageBackend = "jsonl";
            vm.LogLevel = "warning";

            await vm.SaveCommand.ExecuteAsync(null);

            CommonConfig saved = commonStore.Saved!;
            await Assert.That(saved.DefaultProvider).IsEqualTo("mistral");
            await Assert.That(saved.DefaultModel).IsEqualTo("mistral-large");
            await Assert.That(saved.StorageBackend).IsEqualTo("jsonl");
            await Assert.That(saved.LogLevel).IsEqualTo("warning");
        }, commonStore, new RecordingAppStore(UnsetApp()));
    }

    [Test]
    public async Task Save_WritesNothingToTheProcessEnvironment()
    {
        // Sentinel values, so "the VM left them alone" and "the VM wrote the same
        // value back" are distinguishable — a self-restoring write would pass a
        // compare-against-current check.
        (string Name, string Value)[] sentinels =
            [.. ProcessEnvNames.Select((name, i) => (name, value: $"harbor-677-sentinel-{i}"))];
        Dictionary<string, string> before = sentinels.ToDictionary(s => s.name, s => s.value, StringComparer.Ordinal);

        foreach ((string name, string value) in sentinels)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            await InAvaloniaSessionAsync(async vm =>
            {
                vm.DefaultProvider = "cerebras";
                vm.DefaultModel = "cerebras/model";
                vm.StorageBackend = "sqlite";
                vm.LogLevel = "trace";
                vm.OllamaHost = "http://ollama.invalid:11434";

                await vm.SaveCommand.ExecuteAsync(null);

                foreach ((string name, string value) in sentinels)
                {
                    await Assert.That(Environment.GetEnvironmentVariable(name)).IsEqualTo(value)
                        .Because(
                            name + " is process state, and #677 removed the write that used to overwrite it from "
                            + "the settings screen. It is read once while AddHarbor composes (StorageModule, "
                            + "ConfigurationModule, ProviderFactories), so the old write could not have changed the "
                            + "running app — it only leaked the choice into every other component in the process, "
                            + "and into every test that ran after it.");
                }
            }, new RecordingCommonStore(UnsetCommon()), new RecordingAppStore(UnsetApp()));
        }
        finally
        {
            foreach ((string name, string value) in before)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    // ── the wizard has no storage step, so it writes no backend ───────────

    [Test]
    public async Task OnboardingWizard_LeavesTheStorageBackendAlone()
    {
        var store = new RecordingCommonStore(UnsetCommon());
        var persister = new ConfigStoreOnboardingPersister(store);

        Result result = await persister
            .PersistAsync("ollama", "llama3.2", newKey: null, overwriteDefaults: true, ct: CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        CommonConfig saved = store.Saved
            ?? throw new InvalidOperationException("The persister did not write the common config.");

        await Assert.That(saved.StorageBackend).IsEmpty()
            .Because(
                "The wizard has no storage-backend step — it never asks. It used to write \"jsonl\" when the field "
                + "was empty, which is the one value the desktop preset (\"memory\") exists to override, so no "
                + "desktop user could ever reach their own default. And because the wizard re-runs, a user who "
                + "had deliberately unset the field got \"jsonl\" written back by the app itself (#677).");
        await Assert.That(saved.DefaultProvider).IsEqualTo("ollama");
        await Assert.That(saved.DefaultModel).IsEqualTo("llama3.2");
        await Assert.That(saved.OnboardingCompleted).IsTrue();
    }

    [Test]
    public async Task OnboardingWizard_StillHonoursAnExplicitlyChosenBackend()
    {
        var store = new RecordingCommonStore(UnsetCommon() with { StorageBackend = "sqlite" });
        var persister = new ConfigStoreOnboardingPersister(store);

        await persister.PersistAsync("ollama", "llama3.2", newKey: null, overwriteDefaults: true, ct: CancellationToken.None);

        await Assert.That(store.Saved!.StorageBackend).IsEqualTo("sqlite")
            .Because(
                "Not touching the field is not the same as clearing it: a backend the user actually chose must "
                + "survive a wizard re-run. The old line only overwrote an EMPTY value, so this passed before too "
                + "— the point of the assertion is that the removal did not over-correct into a wipe.");
    }

    // ── harness ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Build the settings view-model on an Avalonia UI thread. The headless
    ///     session is the same one <c>ViewInflationTests</c> uses, and it is
    ///     needed because <c>SettingsViewModel</c> composes a
    ///     <c>ThemeSettingsViewModel</c>, whose palette previews are Avalonia
    ///     brushes.
    /// </summary>
    private static async Task InAvaloniaSessionAsync(
        Func<SettingsViewModel, Task> body,
        RecordingCommonStore commonStore,
        RecordingAppStore appStore)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            async () =>
            {
                SettingsViewModel vm = new(
                    new StubThemeReader(),
                    new StubThemeApplier(),
                    NullLogger<SettingsViewModel>.Instance,
                    NullLoggerFactory.Instance,
                    new StubToastService(),
                    commonStore,
                    appStore,
                    new EmptyProviderRegistry(),
                    new StubAuthResolver());

                await body(vm);
            },
            CancellationToken.None);
    }

    // ── stubs ─────────────────────────────────────────────────────────────

    private sealed class RecordingCommonStore(CommonConfig seed) : ICommonConfigStore
    {
        private CommonConfig _current = seed;

        /// <summary>The last snapshot handed to <see cref="SaveAsync" />, or null.</summary>
        public CommonConfig? Saved { get; private set; }

        public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success(_current));

        public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default)
        {
            _current = config;
            Saved = config;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default)
        {
            _current = updater(_current);
            Saved = _current;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class RecordingAppStore(AvaloniaConfig seed) : IAppConfigStore<AvaloniaConfig>
    {
        private AvaloniaConfig _current = seed;

        /// <summary>The last snapshot handed to <see cref="SaveAsync" />, or null.</summary>
        public AvaloniaConfig? SavedApp { get; private set; }

        public Task<Result<AvaloniaConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success(_current));

        public Task<Result> SaveAsync(AvaloniaConfig config, CancellationToken ct = default)
        {
            _current = config;
            SavedApp = config;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdateAsync(Func<AvaloniaConfig, AvaloniaConfig> updater, CancellationToken ct = default)
        {
            _current = updater(_current);
            SavedApp = _current;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubThemeReader : IThemeReader
    {
        public string Current => "dark";
        public bool IsDark => true;
    }

    private sealed class StubThemeApplier : IThemeApplier
    {
        public void Apply(string theme) { }
        public void ApplyDark() { }
        public void ApplyLight() { }
        public void Toggle() { }
        public void ApplyHds(string theme) { }
        public void SetThemeVariant(bool isDark) { }
    }

    private sealed class StubToastService : IToastService
    {
#pragma warning disable CS0067
        public event EventHandler<ToastNotification>? ToastAdded;
#pragma warning restore CS0067

        public void Show(string message, ToastKind kind = ToastKind.Info) { }
    }

    private sealed class StubAuthResolver : IAuthResolver
    {
        public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<string>("no key"));
    }

    /// <summary>
    ///     No providers registered, so <c>SettingsViewModel.LoadProviderConfigs</c>
    ///     adds no rows and the API-key dictionary the save writes is empty.
    /// </summary>
    private sealed class EmptyProviderRegistry : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [];

        public Result<ILlmClient> GetClient(ProviderId providerId)
            => Result.Failure<ILlmClient>("not registered");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
            => GetAllModelsAsync(cancellationToken);

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) => Result.Failure("not registered");
    }
}
