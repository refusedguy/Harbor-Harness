// ProviderAuthSingleAnswerTests.cs — #671.
//
// The defect, in the words of the person who filed it: a provider authorized
// through the environment showed "✗ Нет API-ключа" in the picker and
// "Authenticated" in Settings. One app, one question, two answers.
//
// The two surfaces disagreed because they read different stores. Settings asked
// IAuthResolver — the abstraction its own doc comment calls "the single auth
// abstraction for ALL ILlmClient implementations", and which reads config
// stores, OS keychains, CLI overrides AND conventional environment variables.
// The picker asked the CommonConfig.ApiKeys dictionary, which is one of those
// four stores and not the whole of it. A key in $KILO_API_KEY was simply not
// there to be found.
//
// THE SETUP IS THE POINT. Every test here pairs
//
//   * EmptyApiKeyConfigStore — a config with an EMPTY ApiKeys map, so anything
//     that reads the config to decide auth is guaranteed to say "no key", and
//
//   * EnvOnlyAuthResolver — the truth about the same provider, with the key
//     living only in the environment.
//
// so the two possible answers provably differ. A test that only asserted
// "Authenticated" could be satisfied by a surface that always says
// Authenticated; NoKeysAnywhereAuthResolver is the control that rules that out.
//
// The remaining tests pin the conflation the row made after the fix: reachability
// is not identity. A model count, an empty catalogue, a failed /models call and
// an empty key field are all facts about the world, none of which is a fact
// about whether a credential resolves.

using CommunityToolkit.Mvvm.Messaging;
using Harbor.Abstractions.Providers;
using Harbor.Desktop.Abstractions.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Asserts that the picker and the settings row answer "is this provider
///     authorized?" identically, from <see cref="IAuthResolver" /> alone.
/// </summary>
public class ProviderAuthSingleAnswerTests
{
    /// <summary>A provider that needs a key and has one only in the environment.</summary>
    private const string EnvOnly = "kilocode";

    /// <summary>A provider that needs a key and has one nowhere.</summary>
    private const string Keyless = "anthropic";

    // ── The one-question-one-answer claim ───────────────────────────────────

    [Test]
    public async Task EnvAuthorizedProvider_LooksTheSameInThePickerAndInSettings()
    {
        var auth = new EnvOnlyAuthResolver(EnvOnly);

        ProviderGroupViewModel picker = await LoadPickerRowAsync(auth, EnvOnly);
        ProviderConfigViewModel row = CreateRow(auth, EnvOnly, requiresApiKey: true, isAuthenticated: false);

        // Settings' own constructor verdict was already wrong in the reported
        // world, so hand the row the worst case and let the resolver correct it.
        await row.TestConnectionCommand.ExecuteAsync(null);

        await Assert.That(picker.IsAuthenticated).IsTrue();
        await Assert.That(picker.AuthStatusIcon).IsEqualTo("✓");
        await Assert.That(picker.AuthStatusText).IsEqualTo("Authenticated");

        await Assert.That(row.IsAuthenticated).IsTrue();
        await Assert.That(row.AuthIcon).IsEqualTo("✓");
        await Assert.That(row.AuthText).IsEqualTo("Authenticated");
    }

    [Test]
    public async Task ProviderWithNoKeyAnywhere_LooksTheSameInThePickerAndInSettings()
    {
        // The control for the test above. If this failed while the other passed,
        // "both surfaces agree" would mean "both surfaces always say No key" —
        // which is agreement, but not the agreement anyone wants.
        var auth = new NoKeysAnywhereAuthResolver();

        ProviderGroupViewModel picker = await LoadPickerRowAsync(auth, EnvOnly);
        ProviderConfigViewModel row = CreateRow(auth, EnvOnly, requiresApiKey: true, isAuthenticated: false);

        await row.TestConnectionCommand.ExecuteAsync(null);

        await Assert.That(picker.IsAuthenticated).IsFalse();
        await Assert.That(picker.AuthStatusIcon).IsEqualTo("✗");
        await Assert.That(picker.AuthStatusText).Contains("No API key");

        await Assert.That(row.IsAuthenticated).IsFalse();
        await Assert.That(row.AuthIcon).IsEqualTo("✗");
        await Assert.That(row.AuthText).IsEqualTo("No key");
    }

    [Test]
    public async Task PickerAsksTheResolver_EvenThoughTheConfigFileHoldsNoKey()
    {
        // The narrowest statement of the reported bug: with an EMPTY
        // CommonConfig.ApiKeys, a config-dictionary reader answers "no key".
        // This asserts the resolver was actually consulted, so a future
        // "optimisation" that inlines the dictionary read cannot pass silently.
        var auth = new EnvOnlyAuthResolver(EnvOnly);

        ProviderGroupViewModel picker = await LoadPickerRowAsync(auth, EnvOnly);

        await Assert.That(picker.IsAuthenticated).IsTrue();
        await Assert.That(auth.Asked).Contains(EnvOnly);
    }

    [Test]
    public async Task AProviderThatServesModelsButHasNoKey_RendersTheMissingKeyGlyph()
    {
        // The inverse of the model-count regression, and the direction the
        // picker could have drifted into: /models answers fine, so a reader that
        // inferred auth from reachability would say "Authenticated" — while the
        // resolver, the only thing entitled to an opinion, refuses.
        var auth = new NoKeysAnywhereAuthResolver();

        ProviderGroupViewModel picker = await LoadPickerRowAsync(auth, EnvOnly);

        await Assert.That(picker.Models.Count).IsEqualTo(1);
        await Assert.That(picker.IsAuthenticated).IsFalse();
        await Assert.That(picker.AuthStatusIcon).IsEqualTo("✗");
    }

    // ── Reachability is not identity ───────────────────────────────────────

    [Test]
    public async Task AnEmptyCatalogue_DoesNotUnauthorizeAnEnvAuthorizedProvider()
    {
        // The model-count regression, exactly as it shipped:
        // `row.IsAuthenticated = count > 0 || !row.RequiresApiKey`.
        // A provider with a working key and an empty catalogue reported "No key".
        var auth = new EnvOnlyAuthResolver(EnvOnly);
        var row = CreateRow(
            auth, EnvOnly, requiresApiKey: true, isAuthenticated: true,
            registry: StaticModelRegistry.ServingNothing(EnvOnly));

        await row.TestConnectionCommand.ExecuteAsync(null);

        await Assert.That(row.IsAuthenticated).IsTrue();
        await Assert.That(row.AuthText).IsEqualTo("Authenticated");
        await Assert.That(row.TestResult).Contains("no models returned");
    }

    [Test]
    public async Task AnUnreachableProvider_DoesNotUnauthorizeAnEnvAuthorizedProvider()
    {
        // The other half of the same line: `else { row.IsAuthenticated = false; }`.
        // A network blip reported "No key" for a provider whose key was fine.
        var auth = new EnvOnlyAuthResolver(EnvOnly);
        var row = CreateRow(
            auth, EnvOnly, requiresApiKey: true, isAuthenticated: true,
            registry: StaticModelRegistry.Unreachable(EnvOnly));

        await row.TestConnectionCommand.ExecuteAsync(null);

        await Assert.That(row.IsAuthenticated).IsTrue();
        await Assert.That(row.TestResult).Contains("connection refused");
    }

    [Test]
    public async Task AnEmptyKeyField_DoesNotUnauthorizeAnEnvAuthorizedProvider()
    {
        // The third answer to the same question, in the same file:
        // `row.IsAuthenticated = !string.IsNullOrWhiteSpace(row.ApiKey)`.
        // The field is empty here BECAUSE the key came from the environment —
        // so reading the field de-authorized a working provider.
        var auth = new EnvOnlyAuthResolver(EnvOnly);
        var row = CreateRow(auth, EnvOnly, requiresApiKey: true, isAuthenticated: true, apiKey: string.Empty);

        await row.SaveKeyCommand.ExecuteAsync(null);

        await Assert.That(row.IsAuthenticated).IsTrue();
        await Assert.That(row.AuthText).IsEqualTo("Authenticated");
    }

    [Test]
    public async Task AProbeDoesNotDecideTheVerdict_ItOnlyReportsReachability()
    {
        // "What can this provider answer?" and "is it authorized?" are two
        // questions. This asserts the row kept them apart: a healthy catalogue
        // must not manufacture a verdict the resolver never gave, so start from
        // a provider the resolver refuses and let /models succeed.
        var auth = new NoKeysAnywhereAuthResolver();
        var row = CreateRow(auth, Keyless, requiresApiKey: true, isAuthenticated: false);

        await row.TestConnectionCommand.ExecuteAsync(null);

        await Assert.That(row.IsAuthenticated).IsFalse();
        await Assert.That(row.TestResult).Contains("model(s) available");
    }

    // ── A broken credential source is a verdict, not an outage ──────────────

    [Test]
    public async Task AThrowingResolver_RendersTheMissingKeyGlyphInsteadOfCrashing()
    {
        // A locked keychain or a corrupt store must not take the window down;
        // the row degrades to the ✗ glyph and the user picks another provider.
        var picker = CreatePicker(new ThrowingAuthResolver(), StaticModelRegistry.Serving(EnvOnly, "kilo-auto/free"));

        await picker.LoadCommand.ExecuteAsync(null);

        await Assert.That(picker.IsLoading).IsFalse();
        await Assert.That(picker.ErrorMessage).IsEmpty();
        await Assert.That(picker.AllProviders.Count).IsEqualTo(1);
        await Assert.That(picker.AllProviders[0].IsAuthenticated).IsFalse();
        await Assert.That(picker.AllProviders[0].AuthStatusIcon).IsEqualTo("✗");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static ProviderModelPickerViewModel CreatePicker(IAuthResolver auth, IProviderRegistry registry) =>
        new(registry,
            new EmptyApiKeyConfigStore(),
            new NoSessionsManager(),
            new SilentToastService(),
            NullLogger<ProviderModelPickerViewModel>.Instance,
            WeakReferenceMessenger.Default,
            auth);

    private static ProviderConfigViewModel CreateRow(
        IAuthResolver auth,
        string providerId,
        bool requiresApiKey,
        bool isAuthenticated,
        IProviderRegistry? registry = null,
        string apiKey = "typed-key")
        =>
        new(providerId,
            providerId,
            apiKey,
            requiresApiKey,
            isAuthenticated,
            new EmptyApiKeyConfigStore(),
            registry ?? StaticModelRegistry.Serving(providerId, "some-model"),
            new SilentToastService(),
            NullLogger<ProviderConfigViewModel>.Instance,
            auth);

    private static async Task<ProviderGroupViewModel> LoadPickerRowAsync(IAuthResolver auth, string providerId)
    {
        var picker = CreatePicker(auth, StaticModelRegistry.Serving(providerId, "kilo-auto/free"));
        await picker.LoadCommand.ExecuteAsync(null);

        await Assert.That(picker.AllProviders.Count).IsEqualTo(1);
        return picker.AllProviders[0];
    }
}
