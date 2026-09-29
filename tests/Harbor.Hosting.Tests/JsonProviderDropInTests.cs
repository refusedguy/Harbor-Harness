using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #580 acceptance: <c>providers/&lt;name&gt;.json</c> really is the whole job.
///
///     docs/EXAMPLES.md §9 promises that an OpenAI-compatible provider is added by
///     dropping one JSON file — no csproj edit, no registry row, no code anywhere.
///     These tests execute that promise end to end: a config is placed where the
///     discovery path looks (<c>&lt;exeDir&gt;/providers/</c>), the composition root
///     is built with nothing but that file changed, and the resulting provider is
///     asked for its model list. Pre-#580 this needed a hand-written
///     <c>ProviderPresets</c> row plus a matching entry in three test assertions —
///     forgetting the row was a red build, not a degraded feature.
/// </summary>
[NotInParallel("hosting")]
public class JsonProviderDropInTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-dropin-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(HarborComposeOptions options)
    {
        var services = new ServiceCollection();
        services.AddHarbor(options);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The bundled <c>providers/</c> directory, located by walking up from the test
    ///     binary towards the repo root (same precedence the discovery path uses).
    /// </summary>
    private static string BundledProvidersDirectory()
    {
        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "providers");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "kilocode.json")))
                return candidate;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("bundled providers/ directory not found");
    }

    /// <summary>
    ///     Place <paramref name="configJson" /> where the discovery path reads first, and
    ///     seed the model cache (<c>cache/providers/&lt;id&gt;.json</c> — the exact file the
    ///     catalogue checks before it would reach for the network) so the assertion is
    ///     hermetic. Returns a disposer that removes the dropped file.
    /// </summary>
    private static Action DropConfig(
        string dropFileName, string configJson, string providerId, string modelId, out string harborDir)
    {
        harborDir = TempHarborDir();
        string dropDir = Path.Combine(AppContext.BaseDirectory, "providers");
        string dropPath = Path.Combine(dropDir, dropFileName);
        Directory.CreateDirectory(dropDir);
        File.WriteAllText(dropPath, configJson);

        string cacheDir = Path.Combine(harborDir, "cache", "providers");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(
            Path.Combine(cacheDir, providerId + ".json"),
            $$"""{"data":[{"id":"{{modelId}}","name":"{{modelId}}","context_length":128000}]}""");

        return () => File.Delete(dropPath);
    }

    /// <summary>Assert the drop-in provider is registered and serves its cached model list.</summary>
    private static async Task AssertServesModels(IServiceProvider sp, string providerId, string modelId)
    {
        var registry = sp.GetRequiredService<IProviderRegistry>();

        string registered = string.Join(",", registry.GetRegisteredProviderIds().Select(p => p.Value));
        await Assert.That(registered).Contains(providerId);

        var client = registry.GetClient(ProviderId.Create(providerId));
        await Assert.That(client.IsSuccess).IsTrue();

        var models = await client.Value.GetModelsAsync();
        await Assert.That(models.IsSuccess).IsTrue();

        string modelIds = string.Join(",", models.Value.Select(m => m.Id));
        await Assert.That(modelIds).Contains(modelId);
    }

    // ── the promise ───────────────────────────────────────────────────────

    [Test]
    public async Task BundledConfig_DroppedIntoDiscoveryPath_RegistersAndServesModels_Unchanged()
    {
        // vllm.json copied byte-for-byte — the file the repo already ships, moved
        // nowhere else and edited not at all. Everything below is generic plumbing.
        string source = Path.Combine(BundledProvidersDirectory(), "vllm.json");
        Action cleanup = DropConfig(
            "vllm.json",
            File.ReadAllText(source),
            "vllm",
            "dropped-in-vllm-model",
            out string harborDir);

        try
        {
            using var sp = Compose(new HarborComposeOptions { HarborDir = harborDir, DefaultStorageBackend = "memory" });
            await AssertServesModels(sp, "vllm", "dropped-in-vllm-model");
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task UnknownProviderJson_NeedsNoRegistration_AndServesModels()
    {
        // The strongest form of the claim: a provider this codebase has never heard
        // of, invented in the test body, dropped as a file. If anything had to be
        // declared in C# for it to work, this test could not pass.
        const string config = """
            {
              "id": "harbor580dropin",
              "displayName": "Harbor 580 drop-in",
              "description": "Invented in a test; registered nowhere.",
              "baseUrl": "http://127.0.0.1:1/v1",
              "apiType": "openai-compatible",
              "authType": "bearer",
              "authEnvVar": "HARBOR580DROPIN_API_KEY",
              "modelsUrl": "http://127.0.0.1:1/v1/models",
              "modelsPath": "data",
              "modelMapping": { "id": "id", "displayName": "id" },
              "defaultModel": "dropin-1",
              "setupHint": "No key needed for this test.",
              "priority": 7
            }
            """;

        Action cleanup = DropConfig(
            "harbor580dropin.json", config, "harbor580dropin", "dropin-1", out string harborDir);

        try
        {
            using var sp = Compose(new HarborComposeOptions { HarborDir = harborDir, DefaultStorageBackend = "memory" });
            await AssertServesModels(sp, "harbor580dropin", "dropin-1");
        }
        finally
        {
            cleanup();
        }
    }
}
