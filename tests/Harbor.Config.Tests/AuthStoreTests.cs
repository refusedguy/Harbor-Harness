using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Config.Tests;
/// <summary>
///     Tests for AuthStore — manages per-provider API keys in HarborConfig
///     with env-var fallback.
/// </summary>
/// <remarks>
///     #823: every write here is process-wide state that
///     <see cref="AuthStore" /> reads back, and two of them are read by something
///     wider than this class — <c>ListApiKeysAsync</c> enumerates the ENTIRE
///     process environment (AuthStore.cs:140) and synthesises a provider entry for
///     every <c>*_API_KEY</c> it finds. <c>OnboardingWizardTests</c> is in this
///     same assembly, writes 32 of the same variables, and is not in #823's list of
///     nine. Both carry <c>process-env</c> now.
/// </remarks>
[NotInParallel("process-env")]
public class AuthStoreTests
{
    /// <summary>
    ///     The three literal-named keys this class pins, saved before each test and
    ///     handed back after it (#870, mechanism from #847).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These are the writes whose variable name is a string literal, so the
    ///         class knows up front which names it is about to destroy and can put
    ///         them back without a teardown at each call site. The three tests that
    ///         write through an <c>envName</c> local cannot be here — that name is a
    ///         different provider in each of them, known only inside the method — and
    ///         they save and restore inline instead.
    ///     </para>
    ///     <para>
    ///         <c>ANTHROPIC_API_KEY</c> is the one that costs: it is a real key, on a
    ///         runner that exports it, and this class is in the same assembly as
    ///         <c>OnboardingWizardTests</c>, which reached it first.
    ///     </para>
    /// </remarks>
    private string? _ambientAnthropicApiKey;
    private string? _ambientUnknownProviderApiKey;
    private string? _ambientListTestProviderApiKey;

    [Before(Test)]
    public void SaveAmbientProviderKeys()
    {
        _ambientAnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _ambientUnknownProviderApiKey = Environment.GetEnvironmentVariable("UNKNOWNPROVIDER_API_KEY");
        _ambientListTestProviderApiKey = Environment.GetEnvironmentVariable("LISTTESTPROVIDER_API_KEY");
    }

    [After(Test)]
    public void RestoreAmbientProviderKeys()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _ambientAnthropicApiKey);
        Environment.SetEnvironmentVariable("UNKNOWNPROVIDER_API_KEY", _ambientUnknownProviderApiKey);
        Environment.SetEnvironmentVariable("LISTTESTPROVIDER_API_KEY", _ambientListTestProviderApiKey);
    }

    private static string NewTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-auth-{Guid.NewGuid():N}", "config.json");

    private static (AuthStore auth, JsonConfigStore store, string path) CreateAuthStore()
    {
        string path = NewTempConfigPath();
        var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
        var auth = new AuthStore(store, NullLogger<AuthStore>.Instance);
        return (auth, store, path);
    }

    [Test]
    public async Task GetApiKeyAsync_ReturnsFromConfig_WhenSet()
    {
        (var auth, var store, string path) = CreateAuthStore();
        try
        {
            await auth.SetApiKeyAsync("anthropic", "sk-ant-from-config");

            var result = await auth.GetApiKeyAsync("anthropic");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("sk-ant-from-config");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task GetApiKeyAsync_FallsBackToEnvVar()
    {
        (var auth, _, string path) = CreateAuthStore();
        string envName = "ACMEPROVIDER_API_KEY";
        // #847: `null` is the right value to pin, not the right value to restore.
        // Nothing here ever read the ambient value, so writing null back destroyed
        // the variable for every test that ran after this one.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "env-value-xyz");
        try
        {
            var result = await auth.GetApiKeyAsync("acmeprovider");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("env-value-xyz");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task GetApiKeyAsync_ConfigTakesPriorityOverEnvVar()
    {
        (var auth, _, string path) = CreateAuthStore();
        string envName = "DUALPROVIDER_API_KEY";
        // #847: the key is still exported on a developer machine or a CI runner, and
        // this test's teardown used to destroy it rather than hand it back.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "env-value");
        try
        {
            await auth.SetApiKeyAsync("dualprovider", "config-value");

            var result = await auth.GetApiKeyAsync("dualprovider");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("config-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task GetApiKeyAsync_FailsWhenNeitherConfigNorEnvVar()
    {
        (var auth, _, string path) = CreateAuthStore();
        // Clear any env var that might exist for this provider id.
        Environment.SetEnvironmentVariable("UNKNOWNPROVIDER_API_KEY", null);
        try
        {
            var result = await auth.GetApiKeyAsync("unknownprovider");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("UNKNOWNPROVIDER_API_KEY");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SetApiKeyAsync_PersistsAcrossReload()
    {
        (var auth, var store, string path) = CreateAuthStore();
        try
        {
            await auth.SetApiKeyAsync("openai", "sk-openai-abc");

            // Simulate process restart by creating a new AuthStore on the same file.
            var newStore = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            var newAuth = new AuthStore(newStore, NullLogger<AuthStore>.Instance);
            var result = await newAuth.GetApiKeyAsync("openai");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("sk-openai-abc");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task RemoveApiKeyAsync_DeletesFromConfig()
    {
        (var auth, var store, string path) = CreateAuthStore();
        try
        {
            await auth.SetApiKeyAsync("anthropic", "sk-ant");
            // Sanity check.
            var before = await auth.GetApiKeyAsync("anthropic");
            await Assert.That(before.IsSuccess).IsTrue();

            var removeResult = await auth.RemoveApiKeyAsync("anthropic");
            await Assert.That(removeResult.IsSuccess).IsTrue();

            // After removal, GetApiKeyAsync must fail (no env var either).
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var after = await auth.GetApiKeyAsync("anthropic");
            await Assert.That(after.IsFailure).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ListApiKeysAsync_ShowsConfiguredKeys()
    {
        (var auth, _, string path) = CreateAuthStore();
        // Clean env var that might interfere.
        Environment.SetEnvironmentVariable("LISTTESTPROVIDER_API_KEY", null);
        try
        {
            await auth.SetApiKeyAsync("listtestprovider", "sk-listtest");

            var result = await auth.ListApiKeysAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.ContainsKey("listtestprovider")).IsTrue();
            await Assert.That(result.Value["listtestprovider"]).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LISTTESTPROVIDER_API_KEY", null);
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ListApiKeysAsync_ShowsEnvVarKeys()
    {
        (var auth, _, string path) = CreateAuthStore();
        string envName = "ENVLISTPROVIDER_API_KEY";
        // #847: this is the one that mattered most — ListApiKeysAsync enumerates the
        // ENTIRE process environment (AuthStore.cs:140), so a key destroyed here is
        // gone from a set this very class reads back.
        string? previousEnv = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "from-env");
        try
        {
            var result = await auth.ListApiKeysAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.ContainsKey("envlistprovider")).IsTrue();
            await Assert.That(result.Value["envlistprovider"]).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousEnv);
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SetApiKeyAsync_OverwritesPreviousValue()
    {
        (var auth, _, string path) = CreateAuthStore();
        try
        {
            await auth.SetApiKeyAsync("anthropic", "first-key");
            await auth.SetApiKeyAsync("anthropic", "second-key");

            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var result = await auth.GetApiKeyAsync("anthropic");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEqualTo("second-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            if (File.Exists(path)) File.Delete(path);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
