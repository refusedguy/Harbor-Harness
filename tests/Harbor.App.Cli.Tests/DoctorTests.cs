using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2668 — <c>harbor doctor</c> finding logic. The environment is substituted
///     with delegates, so the acceptance case (a missing key produces a WARN that
///     names the variable) is proved without a real provider, a real key or a
///     real filesystem write.
/// </summary>
public class DoctorDiagnosticsTests
{
    [Test]
    public async Task ApiKeys_MissingKey_Warns_AndNamesEnvVar()
    {
        DoctorCredential[] credentials = [new("kilocode", "KILO_API_KEY", "https://example.test/keys")];

        DoctorCheck check = DoctorDiagnostics.ApiKeys(credentials, static _ => null);

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Warn);
        await Assert.That(check.Detail).Contains("KILO_API_KEY");
        await Assert.That(check.Hint ?? string.Empty).Contains("KILO_API_KEY");
    }

    [Test]
    public async Task ApiKeys_MissingKey_AmongSeveral_NamesEveryMissingVar()
    {
        DoctorCredential[] credentials =
        [
            new("kilocode", "KILO_API_KEY", null),
            new("openai", "OPENAI_API_KEY", null),
            new("ollama", string.Empty, null),
        ];

        DoctorCheck check = DoctorDiagnostics.ApiKeys(
            credentials,
            static name => string.Equals(name, "OPENAI_API_KEY", StringComparison.Ordinal) ? "present" : null);

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Warn);
        await Assert.That(check.Detail).Contains("KILO_API_KEY");
        await Assert.That(check.Detail).DoesNotContain("OPENAI_API_KEY");
        await Assert.That(check.Detail).DoesNotContain("present");
    }

    [Test]
    public async Task ApiKeys_PresentKey_IsOk_AndDoesNotLeakTheValue()
    {
        const string secret = "klo_do_not_print_me";
        DoctorCredential[] credentials = [new("kilocode", "KILO_API_KEY", null)];

        DoctorCheck check = DoctorDiagnostics.ApiKeys(credentials, _ => secret);

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Ok);
        await Assert.That(check.Detail).DoesNotContain(secret);
    }

    [Test]
    public async Task ApiKeys_OnlyLocalProviders_IsOk()
    {
        DoctorCredential[] credentials = [new("ollama", string.Empty, null)];

        DoctorCheck check = DoctorDiagnostics.ApiKeys(credentials, static _ => null);

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Ok);
        await Assert.That(check.Detail).Contains("no key-based providers");
    }

    [Test]
    public async Task Storage_UnwritableDirectory_IsFail_WithActionableHint()
    {
        DoctorCheck check = DoctorDiagnostics.Storage(
            "jsonl", "/nonexistent/harbor", static _ => (false, "permission denied"));

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Fail);
        await Assert.That(check.Detail).Contains("permission denied");
        await Assert.That(check.Hint ?? string.Empty).Contains("HARBOR_STORAGE");
    }

    [Test]
    public async Task Storage_MemoryBackend_NeedsNoDiskProbe()
    {
        DoctorCheck check = DoctorDiagnostics.Storage(
            "memory", "/does/not/exist", static _ => (false, "must not be called"));

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Ok);
        await Assert.That(check.Detail).Contains("no disk access needed");
    }

    [Test]
    public async Task Renderers_Empty_IsFail()
    {
        DoctorCheck check = DoctorDiagnostics.Renderers([]);

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Fail);
        await Assert.That(check.Hint ?? string.Empty).Contains("HARBOR_TUI");
    }

    [Test]
    public async Task Plugins_FailedInventoryEntries_Warn()
    {
        var state = new DoctorPluginState(InventoryPresent: true, Loaded: 2, Failed: 1);

        DoctorCheck check = DoctorDiagnostics.Plugins([], state);

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Warn);
        await Assert.That(check.Detail).Contains("1 failed");
    }

    [Test]
    public async Task Plugins_SourcesWithoutInventory_Warn_AndNameTheCount()
    {
        DoctorCheck check = DoctorDiagnostics.Plugins(
            ["hello.cs", "tools.dll"], new DoctorPluginState(false, 0, 0));

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Warn);
        await Assert.That(check.Detail).Contains("2 plugin source(s)");
    }

    [Test]
    public async Task Config_Unreadable_Warns()
    {
        DoctorCheck check = DoctorDiagnostics.Config("/home/me/.harbor/config.json", true, "access denied");

        await Assert.That(check.Status).IsEqualTo(DoctorStatus.Warn);
        await Assert.That(check.Detail).Contains("access denied");
    }

    [Test]
    public async Task Build_ProducesEverySection_WithSubstituteEnvironment()
    {
        var inputs = new DoctorInputs(
            RuntimeVersion: "10.0.0",
            ConfigPath: "/home/me/.harbor/config.json",
            ConfigExists: false,
            ConfigProblem: null,
            StorageBackend: "jsonl",
            StoragePath: "/home/me/.harbor",
            Renderers: ["ansi", "cellforge", "plain"],
            Credentials: [new("kilocode", "KILO_API_KEY", null)],
            PluginSources: [],
            Plugins: new DoctorPluginState(false, 0, 0));
        var env = new DoctorEnvironment(
            static _ => null,
            static _ => (true, "writable: /home/me/.harbor"));

        IReadOnlyList<DoctorCheck> checks = DoctorDiagnostics.Build(inputs, env);

        await Assert.That(checks.Count).IsEqualTo(6);
        await Assert.That(checks.Select(static check => check.Name)).IsEquivalentTo(
            ["SDK/runtime", "Config", "API keys", "Storage", "Renderers", "Plugins"]);
        await Assert.That(checks.Single(static check => check.Name == "API keys").Status)
            .IsEqualTo(DoctorStatus.Warn);
        await Assert.That(checks.Count(static check => check.Status == DoctorStatus.Fail)).IsEqualTo(0);
    }
}

/// <summary>The verb itself is wired through the single ConsoleAppFramework builder.</summary>
[NotInParallel]
public class DoctorVerbTests : CafVerbTestBase
{
    [Test]
    public async Task Doctor_Help_ExitsZeroWithUsage() =>
        await AssertHelpExitsZeroWithUsage("doctor");

    [Test]
    public async Task Doctor_Bare_PrintsTheReport()
    {
        var (_, output, _) = await RunCafAsync("doctor", []);

        await Assert.That(output).Contains("Harbor doctor");
        await Assert.That(output).Contains("API keys");
        await Assert.That(output).Contains("Summary:");
    }
}
