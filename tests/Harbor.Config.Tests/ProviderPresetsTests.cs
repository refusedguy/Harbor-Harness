using Harbor.Application.Configuration;
namespace Harbor.Config.Tests;
/// <summary>
///     Tests for ProviderPresets — the built-in catalog of provider templates.
/// </summary>
public class ProviderPresetsTests
{
    [Test]
    public async Task All_ContainsThirteenPresets() => await Assert.That(ProviderPresets.All.Count).IsEqualTo(13);

    [Test]
    public async Task All_ContainsExpectedIds()
    {
        string[] ids = ProviderPresets.All.Select(p => p.Id).ToArray();
        await Assert.That(ids).Contains("kilocode");
        await Assert.That(ids).Contains("anthropic");
        await Assert.That(ids).Contains("openai");
        await Assert.That(ids).Contains("openrouter");
        await Assert.That(ids).Contains("deepseek");
        await Assert.That(ids).Contains("groq");
        await Assert.That(ids).Contains("mistral");
        await Assert.That(ids).Contains("xai");
        await Assert.That(ids).Contains("together");
        await Assert.That(ids).Contains("fireworks");
        await Assert.That(ids).Contains("cerebras");
        await Assert.That(ids).Contains("ollama");
        await Assert.That(ids).Contains("vllm");
    }

    [Test]
    public async Task Find_ReturnsPreset_WhenIdMatches()
    {
        var preset = ProviderPresets.Find("anthropic");
        await Assert.That(preset).IsNotNull();
        await Assert.That(preset!.Id).IsEqualTo("anthropic");
        await Assert.That(preset.DisplayName).IsEqualTo("Anthropic (Claude)");
        await Assert.That(preset.RequiresApiKey).IsTrue();
        await Assert.That(preset.EnvVarName).IsEqualTo("ANTHROPIC_API_KEY");
    }

    [Test]
    public async Task Find_IsCaseInsensitive()
    {
        var preset = ProviderPresets.Find("ANTHROPIC");
        await Assert.That(preset).IsNotNull();
        await Assert.That(preset!.Id).IsEqualTo("anthropic");
    }

    [Test]
    public async Task Find_ReturnsNull_ForUnknownId()
    {
        var preset = ProviderPresets.Find("nonexistent-provider");
        await Assert.That(preset).IsNull();
    }

    [Test]
    public async Task GetNoAuth_ReturnsLocalProviders()
    {
        var noAuth = ProviderPresets.GetNoAuth();
        string[] ids = noAuth.Select(p => p.Id).ToArray();

        // Ollama and vLLM are the two local providers without API keys.
        await Assert.That(noAuth.Count).IsEqualTo(2);
        await Assert.That(ids).Contains("ollama");
        await Assert.That(ids).Contains("vllm");
    }

    [Test]
    public async Task GetNoAuth_AllPresetsDoNotRequireApiKey()
    {
        var noAuth = ProviderPresets.GetNoAuth();
        foreach (var p in noAuth)
        {
            await Assert.That(p.RequiresApiKey).IsFalse();
        }
    }

    [Test]
    public async Task GetNoAuth_NoneHaveEnvVarName()
    {
        var noAuth = ProviderPresets.GetNoAuth();
        foreach (var p in noAuth)
        {
            await Assert.That(p.EnvVarName).IsNull();
        }
    }

    [Test]
    public async Task Kilocode_IsFirstInList_AndHasSetupHint()
    {
        var first = ProviderPresets.All[0];
        await Assert.That(first.Id).IsEqualTo("kilocode");
        await Assert.That(first.RequiresApiKey).IsTrue();
        await Assert.That(first.SetupHint).IsNotNull();
    }

    [Test]
    public async Task All_ApiKeyRequiringPresets_HaveEnvVarName()
    {
        foreach (var p in ProviderPresets.All.Where(p => p.RequiresApiKey))
        {
            await Assert.That(p.EnvVarName).IsNotNull();
            await Assert.That(p.SetupHint).IsNotNull();
        }
    }

    // ---- PROD-UI-0 З.1: catalog consistency (presets ↔ providers/*.json) ----

    /// <summary>
    ///     Locate the bundled <c>providers/</c> directory by walking up from
    ///     the test binary towards the repo root (mirrors
    ///     JsonProviderDiscovery.FindProvidersDirectories precedence).
    /// </summary>
    private static string? FindProvidersDirectory()
    {
        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "providers");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "kilocode.json")))
                return candidate;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    [Test]
    public async Task EveryPreset_HasBundledJsonConfig()
    {
        string? dir = FindProvidersDirectory();
        await Assert.That(dir).IsNotNull();

        foreach (var preset in ProviderPresets.All)
        {
            string path = Path.Combine(dir!, $"{preset.Id}.json");
            await Assert.That(File.Exists(path)).IsTrue();
        }
    }

    [Test]
    public async Task BundledJsonConfigs_HaveNoOrphansOutsidePresets()
    {
        string? dir = FindProvidersDirectory();
        await Assert.That(dir).IsNotNull();

        HashSet<string> presetIds = ProviderPresets.All.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(dir!, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            await Assert.That(presetIds.Contains(id)).IsTrue();
        }
    }

    [Test]
    public async Task All_DefaultModels_AreNonEmpty()
    {
        foreach (var p in ProviderPresets.All)
        {
            await Assert.That(string.IsNullOrWhiteSpace(p.DefaultModel)).IsFalse();
        }
    }
}
