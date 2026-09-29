using System.Text.Json;
using Harbor.Application.Configuration;
namespace Harbor.Config.Tests;
/// <summary>
///     Tests for ProviderPresets — the provider picker catalogue, projected from
///     <c>providers/*.json</c> since #580 (there is no hand-maintained table left to
///     police, so the tests pin the projection and the JSON's own invariants instead).
/// </summary>
public class ProviderPresetsTests
{
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

    // ---- PROD-UI-0 З.1: catalogue consistency (providers/*.json ↔ the picker) ----

    /// <summary>
    ///     Locate the bundled <c>providers/</c> directory by walking up from
    ///     the test binary towards the repo root (mirrors
    ///     ProviderPresetCatalog.FindProvidersDirectories precedence).
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
    public async Task All_DefaultModels_AreNonEmpty()
    {
        foreach (var p in ProviderPresets.All)
        {
            await Assert.That(string.IsNullOrWhiteSpace(p.DefaultModel)).IsFalse();
        }
    }

    [Test]
    public async Task BundledJsonConfigs_FileNameMatchesDeclaredId()
    {
        string? dir = FindProvidersDirectory();
        await Assert.That(dir).IsNotNull();

        // Direct JSON invariant, no preset indirection: the id a config declares is
        // the id everything else keys off (env var convention, model ref, registry).
        // A copy-pasted config whose "id" disagrees with its file name is a silent
        // footgun — the preset list, the auth store and the client registry would all
        // speak different names for the same provider.
        foreach (string file in Directory.EnumerateFiles(dir!, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            string? id = doc.RootElement.GetProperty("id").GetString();
            await Assert.That(id).IsEqualTo(Path.GetFileNameWithoutExtension(file));
        }
    }

    // ---- #580: the preset catalogue is DERIVED from providers/*.json ----

    [Test]
    public async Task BundledJsonConfigs_ProjectIntoPresets_WithNoRegistrationStep()
    {
        string? dir = FindProvidersDirectory();
        await Assert.That(dir).IsNotNull();

        // The whole promise of docs/EXAMPLES.md §9 in one assertion: read the bundled
        // configs straight off disk — no preset table, no DI, no registration call —
        // and every one of them shows up as a fully-populated preset.
        string[] files = Directory.GetFiles(dir!, "*.json");
        await Assert.That(files.Length).IsGreaterThan(0);

        var projected = ProviderPresetCatalog.FromJson(files.Select(f => File.ReadAllText(f)));
        await Assert.That(projected.Count).IsEqualTo(files.Length);

        // Fields come from the JSON, not from a second hand-maintained list.
        var kilocode = projected.First(p => p.Id == "kilocode");
        using var kilocodeJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "kilocode.json")));
        await Assert.That(kilocode.DisplayName)
            .IsEqualTo(kilocodeJson.RootElement.GetProperty("displayName").GetString());
        await Assert.That(kilocode.DefaultModel)
            .IsEqualTo(kilocodeJson.RootElement.GetProperty("defaultModel").GetString());
        await Assert.That(kilocode.EnvVarName)
            .IsEqualTo(kilocodeJson.RootElement.GetProperty("authEnvVar").GetString());

        // And the catalogue the app actually ships — ProviderPresetCatalog.Load(),
        // the real directory walk plus the embedded resources — offers every bundled
        // config. "Registered but invisible in the picker" is exactly the #580 bug.
        foreach (string file in files)
        {
            string fileId = Path.GetFileNameWithoutExtension(file);
            await Assert.That(ProviderPresets.All.Any(p => p.Id == fileId)).IsTrue();
        }
    }

    [Test]
    public async Task FromJson_UnregisteredProvider_StillBecomesAPreset()
    {
        // A provider nobody has ever heard of: a bare config, exactly what a user
        // drops into ~/.harbor/providers/. Pre-#580 this needed a row in a C# table
        // plus a matching entry in three assertions; now the JSON is the whole job.
        const string myllm = """
            {
              "id": "myllm",
              "displayName": "My LLM",
              "description": "Fictional provider used by the #580 test.",
              "baseUrl": "https://api.myllm.example/v1",
              "apiType": "openai-compatible",
              "authType": "bearer",
              "authEnvVar": "MYLLM_API_KEY",
              "modelsUrl": "https://api.myllm.example/v1/models",
              "defaultModel": "myllm-medium",
              "setupHint": "Get a key at https://myllm.example/keys",
              "priority": 5
            }
            """;

        var presets = ProviderPresetCatalog.FromJson(new[] { myllm });

        await Assert.That(presets.Count).IsEqualTo(1);
        var preset = presets[0];
        await Assert.That(preset.Id).IsEqualTo("myllm");
        await Assert.That(preset.DisplayName).IsEqualTo("My LLM");
        await Assert.That(preset.Description).IsEqualTo("Fictional provider used by the #580 test.");
        await Assert.That(preset.DefaultModel).IsEqualTo("myllm-medium");
        await Assert.That(preset.RequiresApiKey).IsTrue();
        await Assert.That(preset.EnvVarName).IsEqualTo("MYLLM_API_KEY");
        await Assert.That(preset.SetupHint).IsEqualTo("Get a key at https://myllm.example/keys");
    }

    [Test]
    public async Task FromJson_AuthTypeNone_MeansNoApiKey()
    {
        const string local = """
            {
              "id": "mylocal",
              "displayName": "My Local",
              "baseUrl": "http://localhost:1234/v1",
              "authType": "none",
              "authEnvVar": null,
              "defaultModel": "local-1"
            }
            """;

        var presets = ProviderPresetCatalog.FromJson(new[] { local });

        await Assert.That(presets.Count).IsEqualTo(1);
        await Assert.That(presets[0].RequiresApiKey).IsFalse();
        await Assert.That(presets[0].EnvVarName).IsNull();
        // Unset description falls back to the display name so a panel row is never blank.
        await Assert.That(presets[0].Description).IsEqualTo("My Local");
    }

    [Test]
    public async Task FromJson_PriorityOrdersOnboarding_ThenId()
    {
        const string a = """{ "id": "zzz", "priority": 90, "baseUrl": "http://x/v1" }""";
        const string b = """{ "id": "aaa", "baseUrl": "http://x/v1" }""";
        const string c = """{ "id": "mmm", "priority": 10, "baseUrl": "http://x/v1" }""";

        var presets = ProviderPresetCatalog.FromJson(new[] { a, b, c });

        // Declared priority first (lower = earlier in the picker); a config with no
        // priority sorts after every one that declares it, by id among its peers.
        await Assert.That(string.Join(",", presets.Select(p => p.Id))).IsEqualTo("mmm,zzz,aaa");
    }

    [Test]
    public async Task FromJson_IgnoresMalformedAndIdlessConfigs()
    {
        var presets = ProviderPresetCatalog.FromJson(new[]
        {
            "not json at all",
            "[]",
            """{ "displayName": "No id here" }""",
            """{ "id": "good", "baseUrl": "http://x/v1", "defaultModel": "m" }""",
        });

        await Assert.That(presets.Count).IsEqualTo(1);
        await Assert.That(presets[0].Id).IsEqualTo("good");
    }

    [Test]
    public async Task FromJson_FirstConfigForAnIdWins()
    {
        // User config outranks the bundled copy for the same id (same precedence the
        // registration path applies), so the picker and the registry agree.
        const string bundled = """{ "id": "kilocode", "displayName": "Bundled", "baseUrl": "http://a/v1" }""";
        const string user = """{ "id": "kilocode", "displayName": "Mine", "baseUrl": "http://b/v1" }""";

        var presets = ProviderPresetCatalog.FromJson(new[] { user, bundled });

        await Assert.That(presets.Count).IsEqualTo(1);
        await Assert.That(presets[0].DisplayName).IsEqualTo("Mine");
    }
}
