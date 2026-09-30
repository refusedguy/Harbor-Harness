using System.Globalization;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Config.Tests;
/// <summary>
///     Tests for OnboardingWizard.RunAsync — exercises the full interactive flow
///     using stub Func&lt;string, Task&lt;string&gt;&gt; reader and Action&lt;string&gt; writer.
///     No real stdin/stdout involved.
/// </summary>
/// <remarks>
///     #823: this class is the largest writer of process environment in the whole
///     repository and it was not in the issue's list of nine — thirty-two writes,
///     twenty-four of them <c>OLLAMA_API_KEY</c> and eight
///     <c>ANTHROPIC_API_KEY</c>, in the same assembly as
///     <c>AuthStoreTests</c>, which writes the same variables and reaches
///     <c>AuthStore.ListApiKeysAsync</c>. That method enumerates the entire process
///     environment (AuthStore.cs:140), so the set of providers this wizard sees is
///     a function of timing. Every one of these writes is a null-out of a real
///     provider key, which is why the collision is a flake rather than a wrong
///     answer; it is still a race, and it is now a declared one.
/// </remarks>
[NotInParallel("process-env")]
public class OnboardingWizardTests
{
    /// <summary>
    ///     The three builtin agents, as a registry the wizard can project (#582).
    /// </summary>
    /// <remarks>
    ///     Passed to every wizard these tests build. The agent step reads the
    ///     registry, so a test that constructs the wizard WITHOUT one exercises the
    ///     degraded no-agents path and every "enter 1 and get code" assertion below
    ///     would pass for the wrong reason.
    /// </remarks>
    private static TestAgentRegistry BuiltinAgents() => new(
        AgentDefinition.CodeDefault("model", "provider"),
        AgentDefinition.PlanDefault("model", "provider"),
        AgentDefinition.ExploreDefault("model", "provider"));

    private static (OnboardingWizard wizard, JsonConfigStore store, AuthStore auth, string path) CreateWizard()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(
            store, auth, NullLogger<OnboardingWizard>.Instance, agents: BuiltinAgents());
        return (wizard, store, auth, path);
    }

    /// <summary>
    ///     The menu index the wizard PRINTED for an agent, read back out of the
    ///     captured output.
    /// </summary>
    /// <remarks>
    ///     The tests below used to hardcode "1"/"2"/"3", which is the hand-written
    ///     menu's numbering — the thing #582 removed. The wizard now orders the menu
    ///     (fallback first, the rest by name), so a hardcoded index tests a
    ///     coincidence rather than a contract. Reading the index off the menu the
    ///     wizard actually printed tests the contract instead: whatever number it
    ///     showed for an agent must select that agent.
    /// </remarks>
    private static int MenuIndexOf(IReadOnlyList<string> output, string agentName)
    {
        foreach (string line in output)
        {
            int open = line.IndexOf("[", StringComparison.Ordinal);
            if (open < 0) continue;
            int close = line.IndexOf(']', open);
            if (close < 0) continue;
            if (line[(close + 1)..].TrimStart().StartsWith(agentName + " ", StringComparison.Ordinal))
            {
                return int.Parse(line[(open + 1)..close], CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException(
            $"the wizard never listed '{agentName}'. Menu was:\n{string.Join("\n", output)}");
    }

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Test]
    public async Task RunAsync_LocalProvider_NoApiKey_CompletesSuccessfully()
    {
        (var wizard, var store, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick ollama by id (no API key required), use default model, code agent.
        var responses = new Queue<string>(new[] { "ollama", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        // Clean any env var that might interfere.
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Provider).IsEqualTo("ollama");
            await Assert.That(loaded.Value.Agent).IsEqualTo("code");
            await Assert.That(loaded.Value.Onboarded).IsTrue();
            // Default model is "{provider.Id}/{provider.DefaultModel}".
            await Assert.That(loaded.Value.Model).IsEqualTo("ollama/llama3.2");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_LocalProvider_PickByNumber_Works()
    {
        (var wizard, var store, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Find ollama preset index (1-based, position in ProviderPresets.All).
        int ollamaIndex = ProviderPresets.All.ToList().FindIndex(p => p.Id == "ollama") + 1;
        var responses = new Queue<string>(new[] { ollamaIndex.ToString(), "", "plan" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Provider).IsEqualTo("ollama");
            await Assert.That(loaded.Value.Agent).IsEqualTo("plan");
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- PROD-UI-0 З.2: "test connection" step in the wizard ----

    /// <summary>Fake probe with a canned outcome, records the probed provider ids.</summary>
    private sealed class FakeHealthCheck(
        Harbor.Abstractions.Providers.ProviderHealth? outcome,
        string? error = null) : Harbor.Abstractions.Providers.IProviderHealthCheck
    {
        public List<string> Probed { get; } = [];

        public Task<CSharpFunctionalExtensions.Result<Harbor.Abstractions.Providers.ProviderHealth>> CheckAsync(
            Harbor.Abstractions.Models.Identifiers.ProviderId providerId,
            CancellationToken cancellationToken = default)
        {
            Probed.Add(providerId.Value);
            return Task.FromResult(outcome is not null && error is null
                ? CSharpFunctionalExtensions.Result.Success(outcome.Value)
                : CSharpFunctionalExtensions.Result.Failure<Harbor.Abstractions.Providers.ProviderHealth>(error ?? "?"));
        }
    }

    [Test]
    public async Task RunAsync_HealthCheckSuccess_ReportsConnectionOk_AndContinues()
    {
        var health = new FakeHealthCheck(new Harbor.Abstractions.Providers.ProviderHealth(42, 7));
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance, health);
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        var responses = new Queue<string>(new[] { "ollama", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(health.Probed).Contains("ollama");
            await Assert.That(output.Any(l => l.Contains("Connection OK"))).IsTrue();

            // The check succeeded → the wizard must still persist the config.
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Onboarded).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_HealthCheckFails_WarnsButDoesNotAbort()
    {
        var health = new FakeHealthCheck(null, error: "API key is invalid or missing (401/403 from provider)");
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance, health);
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        var responses = new Queue<string>(new[] { "anthropic", "sk-bad-key", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(output.Any(l => l.Contains("Connection test failed"))).IsTrue();

            // The failed probe must NOT abort: config is still written so the
            // user can fix the key later via /auth or Settings.
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Provider).IsEqualTo("anthropic");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Cleanup(path);
        }
    }

    // ---- PROD-UI-0 З.4: live model list in the wizard's model step ----

    /// <summary>Fake registry serving a canned model list for every provider.</summary>
    private sealed class FakeLiveRegistry(string[] modelIds) : Harbor.Abstractions.Providers.IProviderRegistry
    {
        private bool _clientsDisabled;

        public void DisableClients() => _clientsDisabled = true;

        public IReadOnlyList<Harbor.Abstractions.Models.Identifiers.ProviderId> GetRegisteredProviderIds() => [];

        public CSharpFunctionalExtensions.Result<Harbor.Abstractions.Providers.ILlmClient> GetClient(
            Harbor.Abstractions.Models.Identifiers.ProviderId providerId) =>
            _clientsDisabled
                ? CSharpFunctionalExtensions.Result.Failure<Harbor.Abstractions.Providers.ILlmClient>(
                    $"Provider '{providerId.Value}' is not registered.")
                : new FakeCatalogClient(providerId, modelIds);

        public Task<CSharpFunctionalExtensions.Result<IReadOnlyList<Harbor.Abstractions.Models.ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CSharpFunctionalExtensions.Result.Failure<IReadOnlyList<Harbor.Abstractions.Models.ModelInfo>>("n/a"));

        public Task<CSharpFunctionalExtensions.Result<IReadOnlyList<Harbor.Abstractions.Models.ModelInfo>>> GetModelsCachedAsync(
            Harbor.Abstractions.Models.Identifiers.ProviderId providerId, CancellationToken cancellationToken = default) =>
            GetAllModelsAsync(cancellationToken);

        public void Register(Harbor.Abstractions.Models.Identifiers.ProviderId providerId, Func<Harbor.Abstractions.Providers.ILlmClient> factory) { }

        public CSharpFunctionalExtensions.Result Unregister(Harbor.Abstractions.Models.Identifiers.ProviderId providerId) =>
            CSharpFunctionalExtensions.Result.Failure("n/a");

        private sealed class FakeCatalogClient : Harbor.Abstractions.Providers.ILlmClient
        {
            private readonly string[] _modelIds;

            public FakeCatalogClient(Harbor.Abstractions.Models.Identifiers.ProviderId providerId, string[] modelIds)
            {
                ProviderId = providerId;
                _modelIds = modelIds;
            }

            public Harbor.Abstractions.Models.Identifiers.ProviderId ProviderId { get; }

            public IAsyncEnumerable<Harbor.Abstractions.Events.LlmEvent> StreamAsync(
                Harbor.Abstractions.Providers.LlmRequest request, CancellationToken cancellationToken = default)
            {
                async IAsyncEnumerable<Harbor.Abstractions.Events.LlmEvent> Empty()
                {
                    await Task.CompletedTask;
                    yield break;
                }
                return Empty();
            }

            public Task<CSharpFunctionalExtensions.Result<IReadOnlyList<Harbor.Abstractions.Models.ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(CSharpFunctionalExtensions.Result.Success<IReadOnlyList<Harbor.Abstractions.Models.ModelInfo>>(
                    _modelIds.Select(id => new Harbor.Abstractions.Models.ModelInfo(
                        id, ProviderId.Value, id, 8192, 4096, false, false, false,
                        Harbor.Abstractions.Models.Pricing.Unknown, "openai")).ToList()));
        }
    }

    [Test]
    public async Task RunAsync_LiveModelList_NumberSelection_PicksListedModel()
    {
        var registry = new FakeLiveRegistry(["zzz-first", "aaa-second", "mmm-third"]);
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance, providers: registry);
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick ollama → skip key → live list shown → choose #2 → agent code.
        var responses = new Queue<string>(new[] { "ollama", "2", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Model).IsEqualTo("ollama/aaa-second");
            await Assert.That(output.Any(l => l.Contains("Available models for"))).IsTrue();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_LiveModelList_EmptyInput_DefaultsToPresetModel_WhenPresentInList()
    {
        // ollama preset default is llama3.2 — put it into the live list.
        var registry = new FakeLiveRegistry(["devstral", "llama3.2"]);
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance, providers: registry);
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        var responses = new Queue<string>(new[] { "ollama", "", "", "" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Model).IsEqualTo("ollama/llama3.2");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_UnreachableProvider_DegradesToFreeText()
    {
        // Registry with NO clients → GetClient fails for any id.
        var registry = new FakeLiveRegistry([]);
        registry.DisableClients();
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance, providers: registry);
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        var responses = new Queue<string>(new[] { "ollama", "custom-model", "" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(output.Any(l => l.Contains("manual entry"))).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Model).IsEqualTo("ollama/custom-model");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_ApiKeyProvider_PromptsForKey()
    {
        (var wizard, var store, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick anthropic, enter API key, use default model, code agent.
        var responses = new Queue<string>(new[] { "anthropic", "sk-ant-test-key-123", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        // Clear any env-var fallback so we exercise the prompt branch.
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Provider).IsEqualTo("anthropic");
            await Assert.That(loaded.Value.Onboarded).IsTrue();
            await Assert.That(loaded.Value.ApiKeys["anthropic"]).IsEqualTo("sk-ant-test-key-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_ApiKeyProvider_UsesExistingKey_WhenAlreadySet()
    {
        (var wizard, var store, var auth, string path) = CreateWizard();
        // Pre-set the API key in config — the wizard should detect it and skip the prompt.
        await auth.SetApiKeyAsync("anthropic", "sk-preconfigured");

        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // reader should only be called for: provider, model, agent (no API key prompt).
        var responses = new Queue<string>(new[] { "anthropic", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.ApiKeys["anthropic"]).IsEqualTo("sk-preconfigured");

            // The output should mention "already set".
            string joined = string.Join("\n", output);
            await Assert.That(joined).Contains("already set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_ApiKeyProvider_EmptyKey_Fails()
    {
        (var wizard, _, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick anthropic, then enter empty API key — should fail.
        var responses = new Queue<string>(new[] { "anthropic", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("API key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_CustomModelName_GetsProviderPrefix()
    {
        (var wizard, var store, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick ollama, type a custom model name (no slash), code agent.
        var responses = new Queue<string>(new[] { "ollama", "llama3.3", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            // "llama3.3" (no slash) should be prefixed with provider id.
            await Assert.That(loaded.Value.Model).IsEqualTo("ollama/llama3.3");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_FullyQualifiedModelName_IsPreserved()
    {
        (var wizard, var store, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick ollama, type a fully-qualified model name (already has slash).
        var responses = new Queue<string>(new[] { "ollama", "ollama/qwen2.5:32b", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Model).IsEqualTo("ollama/qwen2.5:32b");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_AgentSelection_ByName_And_ByPrintedIndex()
    {
        // Every registered agent is selectable BOTH ways, and by NAME is the half
        // that is the contract after #582: the menu is projected from the registry,
        // so the name is what the projection offers. By index is read off the menu
        // the wizard printed rather than hardcoded — the wizard orders the menu
        // (fallback first, the rest by name), so a literal "2" would test a
        // coincidence. Both halves together mean: whatever number the wizard shows
        // for an agent, that number selects it, and so does typing its name.
        foreach (string agent in new[] { "code", "plan", "explore" })
        {
            foreach (bool byIndex in new[] { false, true })
            {
                (var wizard, var store, _, string path) = CreateWizard();
                var output = new List<string>();
                Action<string> writer = s => output.Add(s);

                // The agent answer is the LAST thing the wizard reads, so for the
                // index case the menu has to exist first — run the wizard once to
                // learn the numbering, then again with the real answer.
                var probe = new Queue<string>(["ollama", "", ""]);
                await wizard.RunAsync(_ => Task.FromResult(probe.Dequeue()), writer);
                string answer = byIndex
                    ? MenuIndexOf(output, agent).ToString(CultureInfo.InvariantCulture)
                    : agent;

                (var wizard2, var store2, _, string path2) = CreateWizard();
                var output2 = new List<string>();
                var responses = new Queue<string>(["ollama", "", answer]);
                Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

                Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
                try
                {
                    Result result = await wizard2.RunAsync(reader, s => output2.Add(s));

                    await Assert.That(result.IsSuccess).IsTrue();
                    var loaded = await store2.LoadAsync();
                    await Assert.That(loaded.Value.Agent).IsEqualTo(agent)
                        .Because(
                            "the wizard offered " + agent + " and " + (byIndex ? "its printed index" : "its name")
                            + " did not select it. Menu was:\n" + string.Join("\n", output2));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
                    Cleanup(path);
                    Cleanup(path2);
                }
            }
        }
    }

    /// <summary>
    ///     The menu is the registry (#582): every agent the registry holds is listed
    ///     with its own description, and nothing else is. Graded against the registry
    ///     the wizard was given, not against a written list of names.
    /// </summary>
    [Test]
    public async Task RunAsync_AgentMenu_IsTheRegistryProjection()
    {
        (var wizard, _, _, string path) = CreateWizard();
        var output = new List<string>();
        var responses = new Queue<string>(new[] { "ollama", "", "" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            await wizard.RunAsync(reader, output.Add);

            foreach (AgentDefinition agent in BuiltinAgents().GetAllAgents())
            {
                await Assert.That(output.Any(l => l.Contains(agent.Description, StringComparison.Ordinal))).IsTrue()
                    .Because(
                        "each agent's own description has to reach the menu, or the menu is not a "
                        + "projection of the registry but a hand-written list that happens to share "
                        + "its names. Missing: " + agent.Description + ". Menu was:\n"
                        + string.Join("\n", output));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_ListCommand_PrintsAllPresetDetails()
    {
        (var wizard, _, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // First input "list" should print all preset details, then "ollama" picks ollama.
        var responses = new Queue<string>(new[] { "list", "ollama", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            string joined = string.Join("\n", output);
            // "list" should print descriptions for every preset.
            foreach (var preset in ProviderPresets.All)
            {
                await Assert.That(joined).Contains(preset.Id);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_InvalidThenValidProvider_Retries()
    {
        (var wizard, var store, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // First input is invalid; the wizard should retry.
        var responses = new Queue<string>(new[] { "not-a-real-provider", "ollama", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            var loaded = await store.LoadAsync();
            await Assert.That(loaded.Value.Provider).IsEqualTo("ollama");
            // The wizard should have reported the invalid selection.
            string joined = string.Join("\n", output);
            await Assert.That(joined).Contains("Invalid selection");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_WritesWelcomeBanner()
    {
        (var wizard, _, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        var responses = new Queue<string>(new[] { "ollama", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            await wizard.RunAsync(reader, writer);

            string joined = string.Join("\n", output);
            await Assert.That(joined).Contains("Welcome to Harbor");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    [Test]
    public async Task RunAsync_NullRegistry_WarnsLikeClientFailure()
    {
        // ROP boundary #101: the null-registry channel prints the same
        // "model list unavailable … manual entry" warning as the
        // client-failure path instead of degrading silently.
        (var wizard, _, _, string path) = CreateWizard();
        var output = new List<string>();
        Action<string> writer = s => output.Add(s);

        // Pick ollama by id (no API key required), default model, code agent.
        var responses = new Queue<string>(new[] { "ollama", "", "1" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            var result = await wizard.RunAsync(reader, writer);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(output.Any(l => l.Contains("manual entry"))).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    /// <summary>
    ///     A wizard with NO agent registry says so and still answers, rather than
    ///     printing a menu of names it cannot verify (#582).
    /// </summary>
    /// <remarks>
    ///     The old code had no such state: the three names were literals in the
    ///     class, so a wizard built without a registry still offered a menu. The
    ///     degraded path follows the shape the provider and model steps already use
    ///     — print the reason, then continue — and the agent it falls back to is
    ///     <c>AgentName.Fallback</c>, read from the constant rather than spelled.
    /// </remarks>
    [Test]
    public async Task RunAsync_NoAgentRegistry_SaysSo_AndFallsBackToTheDefaultAgent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance);
        var output = new List<string>();
        var responses = new Queue<string>(new[] { "ollama", "", "" });
        Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        try
        {
            Result result = await wizard.RunAsync(reader, output.Add);
            Result<HarborConfig> saved = await store.LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(output.Any(l => l.Contains("No agents registered", StringComparison.Ordinal))).IsTrue()
                .Because(
                    "the step has nothing to list, and a silent empty step is indistinguishable from "
                    + "a wizard that forgot to ask. Menu was:\n" + string.Join("\n", output));

            await Assert.That(saved.IsSuccess).IsTrue()
                .Because("the wizard has to have persisted a choice even with no registry to read");
            if (saved.IsSuccess)
            {
                await Assert.That(saved.Value.Agent).IsEqualTo(AgentName.Fallback)
                    .Because(
                        "an empty registry must not deadlock first-run setup: the wizard still has to "
                        + "answer, and the answer is the agent the core names as the fallback, read from "
                        + "the constant rather than spelled here");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            Cleanup(path);
        }
    }

    /// <summary>
    ///     An <see cref="IAgentRegistry" /> over a fixed list — the production one is
    ///     backed by a <c>ConcurrentDictionary</c>, whose enumeration order is
    ///     unspecified, and these tests care about the wizard's ordering rather than
    ///     the registry's.
    /// </summary>
    private sealed class TestAgentRegistry(params AgentDefinition[] agents) : IAgentRegistry
    {
        private readonly List<AgentDefinition> _agents = [.. agents];

        public IReadOnlyList<AgentDefinition> GetAllAgents() => _agents;

        public Result<AgentDefinition> GetAgent(AgentName name)
        {
            AgentDefinition? found = _agents.FirstOrDefault(a => a.Name.Value == name.Value);
            return found is null
                ? Result.Failure<AgentDefinition>($"Agent '{name}' is not registered.")
                : Result.Success(found);
        }

        public Result Register(AgentDefinition agent) => Result.Success();

        public Result Unregister(AgentName name) => Result.Success();
    }
}
