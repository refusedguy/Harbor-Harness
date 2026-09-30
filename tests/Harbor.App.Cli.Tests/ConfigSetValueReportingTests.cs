// ConfigSetValueReportingTests.cs — issue #709: `/config set` must not report
// success for a value the setters threw away.
//
// THE DEFECT
// ----------
// `ConfigCommand`'s `set` branch assigned through `HarborConfig`'s PROPERTY
// setters and then printed `✓ {key} = {value}` unconditionally:
//
//     case "model":    c.Model    = value;    break;   // `set => _ = TrySetModel(value)`
//     case "maxsteps": if (int.TryParse(value, out int ms)) c.MaxSteps = ms; break;  // no else
//     default: _writer($"Unknown config key: {key}"); break;
//
// Two independent things go wrong there, and neither reaches the user:
//
//   * `Model` / `Provider` / `Agent` parse and DISCARD. `TrySet*` nulls the
//     typed field on failure, so the getter falls back to the built-in default
//     — the user's previously-configured value is not "unchanged", it is
//     ERASED. `✓ model = gpt-4` then reports a write that wiped the setting.
//   * `maxsteps` / `costlimit` have a `TryParse` guard with no `else`, so an
//     unparseable value is dropped without a word.
//
// And the store write itself SUCCEEDS either way: `JsonConfigStore.SaveAsync`
// never calls `Validate()` (only `LoadAsync` does), and nulling a field is a
// valid config. So `updateResult.IsSuccess` is true and the `✗` arm added for
// #603 never fires.
//
// THE CLAIM UNDER TEST
// -------------------
// One invariant, checked for every key rather than for the one the issue
// happened to quote: **the store kept the value if and only if the command
// printed `✓`**. A value the setters discard must be REFUSED, in words, and
// must leave the stored setting alone.
//
// Refusing is the policy this repository already uses, and this file does not
// invent a new one:
//   * `Repl/Commands/ConfigCommand.cs` — the palette `/config` — already
//     refuses `maxsteps`/`costlimit` with `✗ Invalid MaxSteps value: '…'`
//     BEFORE the store is touched. Half this table already had the answer.
//   * `Commands/ModelCommand.cs:81-86` writes `ProviderId.TryCreate`'s own
//     reason and returns `ConvertFailure()` — never a `✓`.
//   * `Commands/AuthCommand.cs:57-61` is `✓` on success, `✗ Failed: …` on
//     failure, and returns the Failure.
//   * `HarborConfig.TrySetProvider` / `TrySetModel` / `TrySetAgent` exist ONLY
//     to return the parse reason ("ROP boundary #101") and had no production
//     caller. The one caller that needs them was this.
//
// The `/model` command DOES take a bare id and qualify it with the current
// provider (`ModelCommand.cs:143-150`). That is not a competing policy, and
// the reason is spelled out in `ModelRef`'s own docs: a bare id is free text
// for a command whose job is "switch to a model", where the provider is
// whatever the user already has. `/config set model` writes one specific
// config KEY; silently substituting a provider the user did not name would
// store a reference they never typed.
//
// WHY THE DISPATCH LEVEL
// ----------------------
// Same argument as `SlashCommandArgumentPassingTests`: a test that builds
// `["set","model","gpt-4"]` itself cannot observe a value being dropped after
// it arrives. Every case below goes through `HandleCoreAsync`, the entry point
// whose output a user's keystrokes actually produce.
//
// The CLI verb needs no separate table. `ConfigVerb.RunAsync` constructs this
// exact class and maps its `Result` to an exit code (`ConfigVerb.cs:30-31`),
// so the `IsFailure` assertions here are the same signal `echo $?` reads. That
// is asserted directly in `RefusedValue_ReturnsFailure_WhichIsTheExitCode`.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Issue #709: the value `/config set` reports as written is the value the
///     store kept.
/// </summary>
public class ConfigSetValueReportingTests
{
    // ── the table, key by key ──────────────────────────────────────────────
    //
    // Every arm of `set`'s switch, with the value that reaches the setter and
    // what must be true of it afterwards. `Stored` is read back off the
    // config the store handed the updater, so a silent no-op cannot pass.

    private static readonly (string Key, string Value, bool Stored, string Because)[] Cases =
    [
        // model — `ModelRef.TryParse` needs `provider/model`
        ("model", "openai/gpt-4", true,
            "A well-formed reference is the value the key accepts."),
        ("model", "gpt-4", false,
            "The issue's headline. One segment cannot satisfy `provider/model`, so the value is discarded — "
            + "and because `TrySetModel` NULLS the field rather than leaving it, the user's previously "
            + "configured model is erased and the getter returns `kilocode/tencent/hy3:free`."),
        ("model", "openai/", false,
            "`Split('/', 2, RemoveEmptyEntries)` drops the empty tail, so this is one segment, not two."),
        ("model", "/gpt-4", false,
            "Same reason from the other side: the leading separator is dropped, leaving one segment."),
        ("model", "bad provider/gpt-4", false,
            "Two segments, but the provider half fails `ProviderId.TryCreate`, so the whole reference is refused."),

        // provider — `ProviderId.TryCreate`, `^[a-z0-9][a-z0-9-]*$`
        ("provider", "openai", true, "The canonical form the key accepts."),
        ("provider", "OpenAI", true,
            "Accepted, and NORMALIZED to lower case. Stored, not discarded — the distortion is documented "
            + "by the assertion on the value, so it cannot become a silent one."),
        ("provider", "bad id!", false,
            "A space and `!` are outside the provider-id alphabet, so the value is discarded and the "
            + "getter returns `IdentityConfig.FallbackProvider`."),

        // agent — `AgentName.TryCreate` rejects only null/blank
        ("agent", "plan", true, "The canonical form the key accepts."),
        ("agent", "PLAN", true,
            "Accepted, and NORMALIZED to lower case. Stored, not discarded."),

        // tui / storage — no parser at all, so nothing is ever discarded
        ("tui", "cellforge", true, "Stored verbatim: this key has no validation to fail."),
        ("storage", "sqlite", true, "Stored verbatim, for the same reason."),

        // maxsteps — `int.TryParse`, and the guard had no `else`
        ("maxsteps", "20", true, "The canonical form the key accepts."),
        ("maxsteps", "abc", false,
            "`int.TryParse` returns false, and with no `else` the value was dropped in silence. The store "
            + "write still succeeded, so `✓ maxsteps = abc` was printed for a value nothing kept."),
        ("maxsteps", "99999999999999", false,
            "`int.TryParse` returns false on OVERFLOW, not only on garbage — so the same silent drop covered "
            + "a value that looks like a number."),

        // costlimit — `decimal.TryParse`, same shape
        ("costlimit", "10.5", true, "The canonical form the key accepts."),
        ("costlimit", "abc", false, "Same silent drop as `maxsteps`."),

        // the default arm — the message existed, the outcome did not
        ("nope", "whatever", false,
            "The `default` arm wrote `Unknown config key: nope` and then fell through to `✓ nope = whatever` "
            + "and `return updateResult`. Two lines, the second one a success, and exit 0.")
    ];

    /// <summary>
    ///     The number of <c>[Arguments]</c> cases below. A mismatch with
    ///     <see cref="Cases" /> is caught loudly inside the test body, because a
    ///     silently-uncovered arm of the table is the exact failure this file
    ///     exists to prevent.
    /// </summary>
    private const int CaseCount = 18;

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    [Arguments(12)]
    [Arguments(13)]
    [Arguments(14)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(17)]
    public async Task EveryKey_ReportsExactlyWhatTheStoreKept(int index)
    {
        await Assert.That(Cases.Length).IsEqualTo(CaseCount)
            .Because(
                "`Cases` and the `[Arguments]` list are two hand-maintained spellings of the same table. If "
                + "they drift, an arm of `set` stops being observed and this file reports a clean sweep over a "
                + "table that is no longer the one in the code.");

        (string key, string value, bool stored, string because) = Cases[index];
        var config = new RecordingConfigStore();
        SeedDistinctConfig(config);

        List<string> lines = await DispatchAsync($"/config set {key} {value}", config);

        bool confirmed = lines.Any(l => l.Contains("✓"));
        bool refused = lines.Any(l => l.Contains("✗"));

        await Assert.That(confirmed).IsEqualTo(stored)
            .Because(
                $"`/config set {key} {value}`: {because} The `✓` line is the command's claim that the value "
                + $"was stored, so a claim with no write behind it is the defect — and a refusal with a `✓` "
                + $"beside it is the same defect wearing a second hat. Refusals must say so in words "
                + $"(observed: {(refused ? "a ✗ line" : "NOTHING")}).");

        await Assert.That(refused).IsEqualTo(!stored)
            .Because(
                $"`/config set {key} {value}`: {because} A value the setters drop must be named, not "
                + $"swallowed — the whole report is that silence and `✓` are indistinguishable to the user.");

        await Assert.That(ReadBack(config, key)).IsEqualTo(stored ? AfterValue(key, value) : BeforeValue(key))
            .Because(
                $"`/config set {key} {value}`: {because} The store is the ground truth, read back off the very "
                + "instance the updater was given. Before the fix a discarded model value still left "
                + "`Model:` reading the built-in default, which is how a rejection that erases a setting hides "
                + "behind a getter that never fails.");
    }

    // ── the headline, named so a failure is legible on its own ──────────────

    [Test]
    public async Task BareModelId_IsRefusedAndLeavesTheStoredModelAlone()
    {
        var config = new RecordingConfigStore();
        config.Current.Model = "openai/gpt-4";

        List<string> lines = await DispatchAsync("/config set model gpt-4", config);

        await Assert.That(config.Current.Model).IsEqualTo("openai/gpt-4")
            .Because(
                "This is the harm the issue describes, and it is an ERASURE rather than a no-op: "
                + "`HarborConfig.TrySetModel` nulls `Identity.Model` on a failed parse, so the previously "
                + "configured model is gone and `/config` reports the built-in default. A value the command "
                + "refuses must leave the setting it refused to change exactly as it was.");

        await Assert.That(lines.Any(l => l.Contains("✓"))).IsFalse()
            .Because("The command is reporting an assignment it did not make.");

        await Assert.That(lines.Any(l => l.Contains("provider/model"))).IsTrue()
            .Because(
                "The refusal has to be actionable. `ModelRef.TryParse` already names the form it wanted — "
                + "\"Invalid model reference 'gpt-4'. Expected 'provider/model'.\" — and reusing the parser's "
                + "own reason is what keeps this from becoming a fourth wording for one rule.");
    }

    [Test]
    public async Task UnparseableMaxSteps_IsRefusedAndLeavesTheStoredValueAlone()
    {
        var config = new RecordingConfigStore();
        config.Current.MaxSteps = 42;

        List<string> lines = await DispatchAsync("/config set maxsteps abc", config);

        await Assert.That(config.Current.MaxSteps).IsEqualTo(42)
            .Because("The `TryParse` guard dropped `abc` before the fix; the stored value has to be untouched.");

        await Assert.That(lines.Any(l => l.Contains("✓"))).IsFalse();
        await Assert.That(lines.Any(l => l.Contains("(expected integer)"))).IsTrue()
            .Because(
                "This is the palette `/config`'s existing wording, verbatim — that command already refused "
                + "this exact case before the store was touched, so the two paths now answer one question the "
                + "same way instead of one refusing and the other confirming.");
    }

    [Test]
    public async Task UnknownKey_IsRefusedRatherThanConfirmed()
    {
        var config = new RecordingConfigStore();

        List<string> lines = await DispatchAsync("/config set nope whatever", config);

        await Assert.That(lines.Any(l => l.Contains("✓"))).IsFalse()
            .Because(
                "The `default` arm wrote `Unknown config key: nope` and then fell through to the success line, "
                + "so the user got the complaint and the confirmation together, in that order, and exit 0.");

        await Assert.That(lines.Any(l => l.Contains("Unknown config key"))).IsTrue();
    }

    /// <summary>
    ///     The exit code, without building a whole host. `ConfigVerb.RunAsync`
    ///     constructs this same class and returns `configResult.IsSuccess ? 0 : 1`
    ///     (`ConfigVerb.cs:30-31`), so the returned <c>Result</c> IS the exit
    ///     code for the CLI verb — there is no second path to test.
    /// </summary>
    [Test]
    public async Task RefusedValue_ReturnsFailure_WhichIsTheExitCode()
    {
        var config = new RecordingConfigStore();
        var lines = new List<string>();

        var command = new Harbor.App.Cli.Commands.ConfigCommand(
            config,
            lines.Add);
        var ctx = new Harbor.App.Cli.Hosting.SimpleCommandContext(
            Session.Create("/harbor-709", "code", "test-provider", "test-model"),
            null!,
            new OfflineProviderRegistry(),
            new FakeToolRegistry(),
            lines.Add,
            _ => Task.FromResult(string.Empty));

        var result = await command.ExecuteAsync(["set", "model", "gpt-4"], ctx);

        await Assert.That(result.IsFailure).IsTrue()
            .Because(
                "`ConfigVerb.RunAsync` maps this `Result` straight to the process exit code, so a refusal that "
                + "returns Success is a script that sees exit 0 for a setting that was not written. The same "
                + "class backs the REPL's `/config`, so one assertion covers both entry points.");

        await Assert.That(lines.Any(l => l.Contains("✓"))).IsFalse();
    }

    [Test]
    public async Task AWellFormedValue_StillReportsSuccess()
    {
        var config = new RecordingConfigStore();

        List<string> lines = await DispatchAsync("/config set model openai/gpt-4", config);

        await Assert.That(lines.Any(l => l.Contains("✓ model = openai/gpt-4"))).IsTrue()
            .Because("Refusing unparseable input is not a reason to start refusing parseable input.");

        await Assert.That(config.Current.Model).IsEqualTo("openai/gpt-4");
    }

    [Test]
    public async Task Usage_ListsTheKeysTheTableAccepts()
    {
        var lines = await DispatchAsync("/config set", new RecordingConfigStore());

        await Assert.That(lines.Any(l => l.Contains("maxsteps"))).IsTrue()
            .Because(
                "An unknown key is a refusal, which makes the key list part of the command's contract instead "
                + "of a detail. The usage block has to name the keys, or `/config set maxstpes 20` fails for "
                + "a reason the command never offered.");
    }

    // ── harness ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Read the key back off the live config. Each key's getter differs, so
    ///     the table is a switch here too — but this one maps a key to a GETTER,
    ///     and it is written out so a new key cannot be added to `set` without
    ///     a test noticing that nothing here can observe it.
    /// </summary>
    /// <remarks>
    ///     A key with no getter here — the unknown-key case — falls back to a
    ///     fingerprint of the WHOLE config, which is the only honest way to say
    ///     "this changed nothing at all".
    /// </remarks>
    private static object ReadBack(RecordingConfigStore store, string key) => key switch
    {
        "model" => store.Current.Model,
        "provider" => store.Current.Provider,
        "agent" => store.Current.Agent,
        "tui" => store.Current.Tui,
        "storage" => store.Current.Storage,
        "maxsteps" => store.Current.MaxSteps,
        "costlimit" => store.Current.CostLimit,
        _ => Fingerprint(store.Current)
    };

    /// <summary>
    ///     Every field this test can set, in one comparable string. Used for the
    ///     unknown-key case, where "the value was refused" means nothing moved.
    /// </summary>
    private static string Fingerprint(HarborConfig config)
        => $"model={config.Model}|provider={config.Provider}|agent={config.Agent}"
           + $"|tui={config.Tui}|storage={config.Storage}|maxsteps={config.MaxSteps}"
           + $"|costlimit={config.CostLimit}";

    /// <summary>What a stored value reads back as, normalization included.</summary>
    private static object AfterValue(string key, string value) => key switch
    {
        // `ProviderId.Create` and `AgentName.Create` both lower-case; the
        // model half of a `ModelRef` is trimmed but keeps its case.
        "provider" => value.ToLowerInvariant(),
        "agent" => value.ToLowerInvariant(),
        "model" => string.Concat(value.Split('/', 2)[0].ToLowerInvariant(), "/", value.Split('/', 2)[1]),
        "maxsteps" => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        "costlimit" => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };

    /// <summary>What a rejected value must leave behind — a value nothing touched.</summary>
    private static object BeforeValue(string key) => key switch
    {
        "model" => "openai/gpt-4",
        "provider" => "openai",
        "agent" => "plan",
        "tui" => "cellforge",
        "storage" => "sqlite",
        "maxsteps" => 7,
        "costlimit" => 3.5m,
        _ => Fingerprint(Seeded())
    };

    /// <summary>
    ///     A starting config whose every field is DISTINCT from the value each
    ///     test sets, so "the store kept it" and "the store still holds the
    ///     seed" can never be confused. <see cref="BeforeValue" /> answers with
    ///     these same values, and it builds them through the same function —
    ///     one definition, not two spellings that can drift.
    /// </summary>
    private static HarborConfig Seeded()
    {
        var config = HarborConfig.Default;
        SeedOn(config);
        return config;
    }

    private static void SeedDistinctConfig(RecordingConfigStore store) => SeedOn(store.Current);

    private static void SeedOn(HarborConfig config)
    {
        config.Model = "openai/gpt-4";
        config.Provider = "openai";
        config.Agent = "plan";
        config.Tui = "cellforge";
        config.Storage = "sqlite";
        config.MaxSteps = 7;
        config.CostLimit = 3.5m;
    }

    private static Task<List<string>> DispatchAsync(string input, RecordingConfigStore config)
    {
        var lines = new List<string>();
        var dispatcher = new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(config, new AuthStore(config)),
            new UnusedPermissionService());

        return CaptureAsync(dispatcher, config, input, lines);
    }

    private static async Task<List<string>> CaptureAsync(
        SlashCommandDispatcher dispatcher, IConfigStore config, string input, List<string> lines)
    {
        SlashCommandOutcome outcome = await dispatcher.HandleCoreAsync(
            input,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            agent: null!, agentRegistry: new FakeAgentRegistry(),
            configStore: config, authStore: new AuthStore(config),
            providers: new OfflineProviderRegistry(),
            session: Session.Create("/harbor-709", "code", "test-provider", "test-model"));

        await Assert.That(outcome.ShouldQuit).IsFalse()
            .Because("A refused value is a command error, not a reason to end the REPL.");

        return lines;
    }

    // ── fakes ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     In-memory store that keeps the very instance the updater was given,
    ///     so a mutation that happened inside <c>UpdateAsync</c> is visible to
    ///     the assertions. Never touches <c>~/.harbor/config.json</c>.
    /// </summary>
    private sealed class RecordingConfigStore : IConfigStore
    {
        public HarborConfig Current { get; } = HarborConfig.Default;

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Success(Current));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            updater(Current);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult(Result.Failure<string>($"No API key for '{providerId}'."));
    }

    /// <summary>Nothing here touches permissions; fail loudly if that changes.</summary>
    private sealed class UnusedPermissionService : IPermissionService
    {
        public Task<Result> SaveAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args,
            CancellationToken ct = default, string? invocationId = null, int generation = 1)
            => throw new NotSupportedException("Not exercised by the config-set tests.");

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the config-set tests.");
    }

    /// <summary>Knows no providers and reaches no network.</summary>
    private sealed class OfflineProviderRegistry : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [];

        public Result<ILlmClient> GetClient(ProviderId providerId)
            => Result.Failure<ILlmClient>($"Provider '{providerId}' is not registered.");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(
            ProviderId providerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public void Register(ProviderId providerId, Func<ILlmClient> factory)
        {
        }

        public Result Unregister(ProviderId providerId)
            => Result.Failure("OfflineProviderRegistry does not support unregister.");
    }
}
