using System.Net;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Providers.Tests;

/// <summary>
/// Tests for ProviderConfig.LoadFromFile — JSON parsing and validation.
/// </summary>
public class ProviderConfigTests
{
    private static string WriteTempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbor-provider-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task LoadFromFile_ValidJson_ReturnsSuccess()
    {
        var json = """
        {
          "id": "openrouter",
          "displayName": "OpenRouter",
          "description": "Multi-provider router",
          "baseUrl": "https://openrouter.ai/api/v1",
          "apiType": "openai-compatible",
          "authType": "bearer",
          "authEnvVar": "OPENROUTER_API_KEY",
          "modelsUrl": "https://openrouter.ai/api/v1/models",
          "modelsRefreshHours": 24,
          "modelsPath": "data",
          "timeout": 120,
          "retries": 3
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsEqualTo("openrouter");
            await Assert.That(result.Value.DisplayName).IsEqualTo("OpenRouter");
            await Assert.That(result.Value.BaseUrl).IsEqualTo("https://openrouter.ai/api/v1");
            await Assert.That(result.Value.AuthEnvVar).IsEqualTo("OPENROUTER_API_KEY");
            await Assert.That(result.Value.ModelsRefreshHours).IsEqualTo(24);
            await Assert.That(result.Value.Timeout).IsEqualTo(120);
            await Assert.That(result.Value.Retries).IsEqualTo(3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_AllowsCommentsAndTrailingCommas()
    {
        // JsonOptions has AllowTrailingCommas=true and ReadCommentHandling=Skip.
        var json = """
        {
          // This is a comment
          "id": "deepseek",
          "displayName": "DeepSeek",
          "baseUrl": "https://api.deepseek.com/v1",
          "authEnvVar": "DEEPSEEK_API_KEY",
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id).IsEqualTo("deepseek");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_InvalidJson_ReturnsFailure()
    {
        var path = WriteTempJson("{ this is not valid json ]");
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("Failed to load provider config");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_MissingId_ReturnsFailure()
    {
        var json = """
        {
          "displayName": "NoId",
          "baseUrl": "https://api.example.com"
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("missing 'id'");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_MissingBaseUrl_ReturnsFailure()
    {
        var json = """
        {
          "id": "test",
          "displayName": "NoBaseUrl"
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var result = ProviderConfig.LoadFromFile(path);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("missing 'baseUrl'");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task LoadFromFile_NonexistentPath_ReturnsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbor-nonexistent-{Guid.NewGuid():N}.json");
        var result = ProviderConfig.LoadFromFile(path);

        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task GetProviderId_ReturnsCreatedProviderId()
    {
        var config = new ProviderConfig
        {
            Id = "groq",
            DisplayName = "Groq",
            BaseUrl = "https://api.groq.com/openai/v1",
        };
        var pid = config.GetProviderId();

        await Assert.That(pid.Value).IsEqualTo("groq");
    }
}

/// <summary>
/// Tests for EnvVarAuthResolver — verifies env var lookup, overrides, and provider-id normalization.
/// </summary>
public class EnvVarAuthResolverTests
{
    [Test]
    public async Task ResolveApiKeyAsync_OverrideTakesPriority()
    {
        var resolver = new EnvVarAuthResolver(new Dictionary<string, string>
        {
            ["anthropic"] = "override-key-123",
        });

        var result = await resolver.ResolveApiKeyAsync("anthropic");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("override-key-123");
    }

    [Test]
    public async Task ResolveApiKeyAsync_EnvVarUsedWhenNoOverride()
    {
        var envName = "TESTPROVIDER_API_KEY";
        Environment.SetEnvironmentVariable(envName, "env-key-456");
        try
        {
            var resolver = new EnvVarAuthResolver();
            var result = await resolver.ResolveApiKeyAsync("testprovider");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("env-key-456");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_NormalizesDashesToUnderscores()
    {
        // Provider id "kilo-code" should map to env var "KILO_CODE_API_KEY".
        var envName = "KILO_CODE_API_KEY";
        Environment.SetEnvironmentVariable(envName, "kilo-key");
        try
        {
            var resolver = new EnvVarAuthResolver();
            var result = await resolver.ResolveApiKeyAsync("kilo-code");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("kilo-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_FailsWhenNeitherOverrideNorEnvVar()
    {
        var envName = "MISSINGPROVIDER_API_KEY";
        Environment.SetEnvironmentVariable(envName, null);
        var resolver = new EnvVarAuthResolver();

        var result = await resolver.ResolveApiKeyAsync("missingprovider");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("MISSINGPROVIDER_API_KEY");
    }

    [Test]
    public async Task ResolveApiKeyAsync_EmptyEnvVar_Fails()
    {
        var envName = "EMPTYPROVIDER_API_KEY";
        Environment.SetEnvironmentVariable(envName, "");
        try
        {
            var resolver = new EnvVarAuthResolver();
            var result = await resolver.ResolveApiKeyAsync("emptyprovider");

            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Test]
    public async Task ResolveApiKeyAsync_ErrorMentionsEnvVarName()
    {
        var resolver = new EnvVarAuthResolver();
        var result = await resolver.ResolveApiKeyAsync("neuro-flash");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("NEURO_FLASH_API_KEY");
    }
}

/// <summary>
/// Tests for DynamicModelCatalog — verifies parsing of fetched responses
/// using a stub HttpMessageHandler (no real network).
/// </summary>
public class DynamicModelCatalogTests
{
    private static string NewCacheDir() =>
        Path.Combine(Path.GetTempPath(), $"harbor-catalog-{Guid.NewGuid():N}");

    [Test]
    public async Task GetModelsAsync_HardcodedModels_ReturnedDirectly()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "static",
                DisplayName = "Static",
                BaseUrl = "https://api.example.com",
                Models = new List<ModelInfo>
                {
                    new("m1", "static", "Model 1", 8192, 4096, false, false, true, Pricing.Unknown, "openai"),
                    new("m2", "static", "Model 2", 32768, 8192, false, true, true, Pricing.Unknown, "openai"),
                },
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Id).IsEqualTo("m1");
            await Assert.That(result.Value[1].Id).IsEqualTo("m2");
            // Should not have made any HTTP calls when models are hardcoded.
            await Assert.That(handler.CapturedRequests.Count).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_NoUrlNoModels_ReturnsFailure()
    {
        var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "empty",
                DisplayName = "Empty",
                BaseUrl = "https://api.example.com",
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("no modelsUrl");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_ParsesDataArray()
    {
        // Standard OpenAI-compatible /v1/models response shape.
        var json = """
        {
          "data": [
            { "id": "gpt-4o", "name": "GPT-4o", "context_length": 128000 },
            { "id": "gpt-4o-mini", "name": "GPT-4o mini", "context_length": 128000 }
          ]
        }
        """;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "openrouter",
                DisplayName = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                ModelsUrl = "https://openrouter.ai/api/v1/models",
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Id).IsEqualTo("gpt-4o");
            await Assert.That(result.Value[0].DisplayName).IsEqualTo("GPT-4o");
            await Assert.That(result.Value[0].ContextWindow).IsEqualTo(128_000);
            await Assert.That(result.Value[1].Id).IsEqualTo("gpt-4o-mini");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_ParsesModelsArray_WhenNoDataField()
    {
        var json = """
        {
          "models": [
            { "id": "llama3.2", "name": "Llama 3.2", "context_length": 4096 }
          ]
        }
        """;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "ollama",
                DisplayName = "Ollama",
                BaseUrl = "http://localhost:11434",
                ModelsUrl = "http://localhost:11434/api/tags",
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].Id).IsEqualTo("llama3.2");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_ParsesModelsPath_NestedProperty()
    {
        // ProviderConfig.ModelsPath "data" — like OpenRouter.
        var json = """
        {
          "data": [
            { "id": "anthropic/claude-3.5-sonnet", "name": "Claude 3.5 Sonnet", "context_length": 200000 }
          ]
        }
        """;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "openrouter",
                DisplayName = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                ModelsUrl = "https://openrouter.ai/api/v1/models",
                ModelsPath = "data",
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].Id).IsEqualTo("anthropic/claude-3.5-sonnet");
            await Assert.That(result.Value[0].ContextWindow).IsEqualTo(200_000);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_UsesModelMapping_ForCustomFieldNames()
    {
        // Ollama-style: top-level "models" array, name field, context_length.
        var json = """
        {
          "models": [
            {
              "name": "llama3.2:latest",
              "model_info": { "context_length": 4096 }
            }
          ]
        }
        """;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "ollama",
                DisplayName = "Ollama",
                BaseUrl = "http://localhost:11434",
                ModelsUrl = "http://localhost:11434/api/tags",
                ModelMapping = new ModelMapping
                {
                    Id = "name",
                    DisplayName = "name",
                    ContextWindow = "model_info.context_length",
                },
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(1);
            await Assert.That(result.Value[0].Id).IsEqualTo("llama3.2:latest");
            await Assert.That(result.Value[0].ContextWindow).IsEqualTo(4096);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_HttpFailure_ReturnsFailure()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error", Encoding.UTF8),
        });
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "broken",
                DisplayName = "Broken",
                BaseUrl = "https://api.broken.com",
                ModelsUrl = "https://api.broken.com/v1/models",
            };

            var result = await catalog.GetModelsAsync(config);

            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    [Test]
    public async Task GetModelsAsync_PersistsCacheFile()
    {
        var json = """
        { "data": [ { "id": "m1", "name": "M1", "context_length": 8192 } ] }
        """;
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler);
        var cacheDir = NewCacheDir();
        try
        {
            var catalog = new DynamicModelCatalog(http, cacheDir, NullLogger<DynamicModelCatalog>.Instance);
            var config = new ProviderConfig
            {
                Id = "cachedprov",
                DisplayName = "Cached",
                BaseUrl = "https://api.example.com",
                ModelsUrl = "https://api.example.com/models",
            };

            var first = await catalog.GetModelsAsync(config);
            await Assert.That(first.IsSuccess).IsTrue();

            var cacheFile = Path.Combine(cacheDir, "cachedprov.json");
            await Assert.That(File.Exists(cacheFile)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }
}
