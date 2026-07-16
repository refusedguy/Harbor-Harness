using Harbor.Core.Configuration;
using Harbor.Core.Onboarding;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Config.Tests;
/// <summary>
///     Tests for OnboardingWizard.RunAsync — exercises the full interactive flow
///     using stub Func&lt;string, Task&lt;string&gt;&gt; reader and Action&lt;string&gt; writer.
///     No real stdin/stdout involved.
/// </summary>
public class OnboardingWizardTests
{
    private static (OnboardingWizard wizard, JsonConfigStore store, AuthStore auth, string path) CreateWizard()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-onboarding-{Guid.NewGuid():N}", "config.json");
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        var wizard = new OnboardingWizard(store, auth, NullLogger<OnboardingWizard>.Instance);
        return (wizard, store, auth, path);
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
        var responses = new Queue<string>(new[] { ollamaIndex.ToString(), "", "2" });
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
    public async Task RunAsync_AgentSelection_Plan_Explored()
    {
        // Exercise all three agent modes by parameterizing.
        foreach ((string input, string expected) in new[] { ("1", "code"), ("2", "plan"), ("3", "explore") })
        {
            (var wizard, var store, _, string path) = CreateWizard();
            var output = new List<string>();
            Action<string> writer = s => output.Add(s);

            var responses = new Queue<string>(new[] { "ollama", "", input });
            Func<string, Task<string>> reader = _ => Task.FromResult(responses.Dequeue());

            Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
            try
            {
                await wizard.RunAsync(reader, writer);
                var loaded = await store.LoadAsync();
                await Assert.That(loaded.Value.Agent).IsEqualTo(expected);
            }
            finally
            {
                Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
                Cleanup(path);
            }
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
}
