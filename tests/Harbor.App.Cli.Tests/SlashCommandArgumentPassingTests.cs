// SlashCommandArgumentPassingTests.cs — issue #650: a delegated slash command
// must SEE the arguments the user typed.
//
// THE DEFECT
// ----------
// `SlashCommandDispatcher` collects handlers as
// `Func<CommandContext, IReadOnlyList<string>, Task<Result>>` and
// `HandleCoreAsync` splits the line correctly:
//
//     string[] args = parts.Skip(1).ToArray();          // "/config set model gpt-4"
//     return ExecuteRegisteredAsync(reg, ctx, args);    //   → ["set","model","gpt-4"]
//
// Five registrations then threw those arguments away at the call site:
//
//     Register(dict, "config", (ctx, _) =>
//     {
//         return new ConfigCommand(...)
//             .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));   // ← args dropped
//     });
//
// So `/auth`, `/model`, `/agent`, `/config` and `/permissions` ALWAYS took their
// no-args branch. `/config set model gpt-4` printed the whole config dump — with
// `Model:` still showing the OLD value — which reads as "here is your
// configuration, and the switch you just asked for did not change it"; the user
// leaves believing either that the model moved (the command clearly ran) or that
// Harbor ignores them. The other four were quieter no-ops: `/auth set …` printed
// a preset list, `/model <p> <m>` printed every available model, `/agent <name>`
// printed the agent list, `/permissions <tool> <pat> <allow>` printed usage.
//
// WHY THIS FILE IS DISPATCH-LEVEL
// -------------------------------
// `SlashCommandFailureSurfacesTests` (#603) already covered the failure arms
// inside AuthCommand / ConfigCommand / PermissionsCommand, but it called those
// classes DIRECTLY with hand-built `["set","model","gpt-4"]` argument lists. That
// is precisely the level at which the loss is invisible: the command classes
// were always correct about their arguments, and the tests passed while every
// one of the five commands was a no-op in the REPL. A test that constructs the
// arguments itself cannot observe the argument not arriving, so the suite was
// green and the product was broken.
//
// Every test here therefore goes through the real
// `HandleCoreAsync("/… args …")` entry point, which is the only level at which
// "did the arguments arrive" is a question with an answer. Each test asserts BOTH
// halves, because only together do they pin the defect:
//
//   positive — the argument-taking branch ran and produced its own output
//              ("✓ model = …"), and
//   negative — the no-args branch did NOT (no "Current configuration:" dump),
//
// A test asserting only the positive half would still pass on a command that
// happened to print something in both cases; the negative half is what makes
// "it took the right branch" observable.
//
// The in-memory config and permission stores keep the tests off the developer's
// real ~/.harbor/config.json — the same reason the #603 fakes exist.

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
///     Issue #650: the five delegated slash commands must receive the arguments
///     the user typed, and the REPL must show the effect of them.
/// </summary>
public class SlashCommandArgumentPassingTests
{
    /// <summary>
    ///     The issue's headline case, verbatim. Before the fix this printed the
    ///     full config dump with <c>Model:</c> still showing the old value.
    /// </summary>
    /// <remarks>
    ///     #709 changed the input. This test asks whether the ARGUMENTS reached
    ///     the command, and the argument-dropped bug made <c>gpt-4</c> a
    ///     convenient witness — it took the <c>set</c> branch. It is now a
    ///     REFUSED value (a bare id is not a <c>provider/model</c>), so it still
    ///     proves argument delivery, but through the other branch. A well-formed
    ///     reference keeps this test on the branch it describes; the refused
    ///     half is
    ///     <see cref="ConfigSetValueReportingTests.BareModelId_IsRefusedAndLeavesTheStoredModelAlone" />.
    /// </remarks>
    [Test]
    public async Task ConfigSet_TakesTheSetBranch_NotTheConfigDump()
    {
        var config = new InMemoryConfigStore();
        List<string> lines = await DispatchAsync("/config set model openai/gpt-4", config);

        await Assert.That(lines.Any(l => l.Contains("✓ model = openai/gpt-4"))).IsTrue()
            .Because(
                "The arguments reached the command, so it took the `/config set` branch. With them dropped it "
                + "took the no-args branch and dumped the configuration instead — showing `Model:` with the OLD "
                + "value, which is the case where a user believes the switch worked when it never ran.");

        await Assert.That(lines.Any(l => l.Contains("Current configuration:"))).IsFalse()
            .Because(
                "This is the negative half, and it is what makes the positive half mean something: the "
                + "no-args branch prints a header that no argument-taking branch prints. A test that only "
                + "asserted the positive line would pass on a command that printed both.");
    }

    /// <summary>
    ///     The store must actually receive the typed value, not merely echo it.
    ///     Uses a well-formed <c>provider/model</c> reference so the assertion
    ///     isolates argument delivery from
    ///     <see cref="HarborConfig.TrySetModel" />'s own parse rules (see the
    ///     note on <see cref="BareModelId_IsRejectedByTheSetter" />).
    /// </summary>
    [Test]
    public async Task ConfigSet_WritesTheValueTheUserTyped()
    {
        var config = new InMemoryConfigStore();
        _ = await DispatchAsync("/config set model openai/gpt-4", config);

        await Assert.That(config.Current.Model).IsEqualTo("openai/gpt-4")
            .Because(
                "`ConfigCommand` routes `set <key> <value>` through `IConfigStore.UpdateAsync`. Echoing the "
                + "line without persisting the value is a different defect, and this is the half that says the "
                + "arguments reached the mutation rather than only the writer.");
    }

    /// <summary>
    ///     Issue #650's neighbour, and the test #650 left here on purpose.
    ///     <see cref="HarborConfig.Model" />'s setter is
    ///     <c>_ = TrySetModel(value)</c>, so when <c>ModelRef.TryParse</c> rejects
    ///     a value the field is NULLed and the getter falls back to the built-in
    ///     default — while the command reported success. The comment here used to
    ///     say the behaviour was pinned "so fixing it turns this test red on
    ///     purpose", and #709 is that fix.
    /// </summary>
    /// <remarks>
    ///     The claim being pinned did not change; the outcome did. A bare model id
    ///     is still not a value the setter keeps — that is the assertion — but it
    ///     is now REFUSED in words and the previously configured model survives,
    ///     instead of being reported as written and erased. Before the fix this
    ///     was not a no-op: a nulled field is a DELETION, so the user's own model
    ///     was replaced by the built-in default.
    /// </remarks>
    [Test]
    public async Task BareModelId_IsRefusedByTheSetter_AndTheStoredModelSurvives()
    {
        var config = new InMemoryConfigStore();
        config.Current.Model = "openai/gpt-4";

        List<string> lines = await DispatchAsync("/config set model gpt-4", config);

        await Assert.That(config.Current.Model).IsEqualTo("openai/gpt-4")
            .Because(
                "Measured, not assumed: `ModelRef.TryParse(\"gpt-4\")` needs `provider/model`, so "
                + "`TrySetModel` takes its failure branch and nulls `Identity.Model`. That made the getter "
                + "return `IdentityConfig.FallbackModel` and left the user reading `✓ model = gpt-4` — an "
                + "ERASURE dressed as a write. #709 decides the key before touching the config and refuses "
                + "the value, so the stored model is untouched.");

        await Assert.That(lines.Any(l => l.Contains("✓"))).IsFalse()
            .Because("Nothing was written, so the command must not report that something was.");

        await Assert.That(lines.Any(l => l.Contains("provider/model"))).IsTrue()
            .Because(
                "The refusal names the form it wanted. Silent and refused look identical to a user, so the "
                + "reason is the whole difference between them.");
    }

    [Test]
    public async Task AuthSet_SavesTheKeyTheUserTyped()
    {
        var config = new InMemoryConfigStore();
        List<string> lines = await DispatchAsync("/auth set acme sk-test-key", config);

        await Assert.That(config.Current.ApiKeys.TryGetValue("acme", out string? key)).IsTrue()
            .Because(
                "`/auth set <provider> <key>` reaches `AuthStore.SetApiKeyAsync(provider, key)`. With the "
                + "arguments dropped the command took the no-args branch, printed the provider-preset list, "
                + "and stored nothing — so the key the user pasted was silently lost.");

        await Assert.That(key).IsEqualTo("sk-test-key");
    }

    [Test]
    public async Task AuthSet_DoesNotTakeTheNoArgsBranch()
    {
        var config = new InMemoryConfigStore();
        List<string> lines = await DispatchAsync("/auth set acme sk-test-key", config);

        await Assert.That(lines.Any(l => l.Contains("Available provider presets:"))).IsFalse()
            .Because(
                "That header belongs to the no-args branch, and it is the one line `/auth set` and a bare "
                + "`/auth` share. Without this the positive assertion could be satisfied by a command that "
                + "printed the usage text and then happened to save something.");
    }

    [Test]
    public async Task ModelSwitch_PersistsTheModelTheUserTyped()
    {
        var config = new InMemoryConfigStore();
        List<string> lines = await DispatchAsync("/model prov-b/model-b", config);

        await Assert.That(config.Current.Model).IsEqualTo("prov-b/model-b")
            .Because(
                "`/model <provider/model>` is the switch command. With the arguments dropped it printed the "
                + "full model catalog instead of switching, and the user got a long list where they expected a "
                + "confirmation.");

        await Assert.That(lines.Any(l => l.Contains("prov-b/model-b"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("All available models"))).IsFalse()
            .Because("That header is the no-args branch; its absence is what proves the switch branch ran.");
    }

    [Test]
    public async Task AgentSwitch_PersistsTheAgentTheUserTyped()
    {
        var config = new InMemoryConfigStore();
        List<string> lines = await DispatchAsync("/agent plan", config);

        await Assert.That(config.Current.Agent).IsEqualTo("plan")
            .Because(
                "`/agent <name>` is how a session changes mode. With the arguments dropped it printed the "
                + "available-agent list, so the mode never changed and the output looked like a successful "
                + "answer to a question that was not the one asked.");

        await Assert.That(lines.Any(l => l.Contains("Available agents:"))).IsFalse()
            .Because("That header is the no-args branch; its absence is what proves the switch branch ran.");
    }

    [Test]
    public async Task PermissionsSet_ReachesTheRuleWriter()
    {
        var permissions = new RecordingPermissionService();
        List<string> lines = await DispatchAsync("/permissions bash src/** deny", new InMemoryConfigStore(), permissions);

        await Assert.That(lines.Any(l => l.Contains("Set permission: bash src/**"))).IsTrue()
            .Because(
                "`/permissions <tool> <pattern> <action>` is the rule editor. With the arguments dropped it "
                + "printed the usage block, so a rule the user carefully typed was never created and the "
                + "command looked like it had read them.");

        await Assert.That(lines.Any(l => l.Contains("  /permissions clear"))).IsFalse()
            .Because("The usage block is the no-args fallback; its absence is what proves the rule branch ran.");

        await Assert.That(permissions.Saves).IsEqualTo(1)
            .Because("SetRule persists through the permission service; a rule that was never saved is not a rule.");
    }

    [Test]
    public async Task PermissionsClear_ClearsInsteadOfListing()
    {
        var permissions = new RecordingPermissionService();
        List<string> lines = await DispatchAsync("/permissions clear", new InMemoryConfigStore(), permissions);

        await Assert.That(lines.Any(l => l.Contains("Cleared all persisted permission rules"))).IsTrue()
            .Because("`/permissions clear` is a verb, not a rule, and only the `clear` branch can say this.");

        await Assert.That(lines.Any(l => l.Contains("Permissions for agent"))).IsFalse()
            .Because("That header is the no-args LIST branch — the command that ran instead of the clear.");
    }

    [Test]
    public async Task EveryDelegatedCommand_StillWorksWithNoArguments()
    {
        // The mirror image of the defect: threading the arguments must not turn
        // a bare `/config` (or `/auth`, `/model`, `/agent`, `/permissions`) into
        // something else. Each of these is the no-args branch, reached with an
        // empty list — which is exactly what the fixed registrations now pass
        // through, and exactly what they used to pass unconditionally.
        var config = new InMemoryConfigStore();
        var permissions = new RecordingPermissionService();

        List<string> configDump = await DispatchAsync("/config", config);
        List<string> authHelp = await DispatchAsync("/auth", config);
        List<string> modelCatalog = await DispatchAsync("/model", config);
        List<string> agentList = await DispatchAsync("/agent", config);
        List<string> permissionList = await DispatchAsync("/permissions", config, permissions);

        await Assert.That(configDump.Any(l => l.Contains("Current configuration:"))).IsTrue();
        await Assert.That(authHelp.Any(l => l.Contains("Available provider presets:"))).IsTrue();
        await Assert.That(modelCatalog.Any(l => l.Contains("All available models"))).IsTrue();
        await Assert.That(agentList.Any(l => l.Contains("Available agents:"))).IsTrue();
        await Assert.That(permissionList.Any(l => l.Contains("Permissions for agent"))).IsTrue();
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static Task<List<string>> DispatchAsync(
        string input,
        InMemoryConfigStore config,
        IPermissionService? permissions = null)
    {
        var lines = new List<string>();
        IPermissionService service = permissions ?? new RecordingPermissionService();

        var dispatcher = new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(config, new AuthStore(config)),
            service);

        return CaptureAsync(dispatcher, config, input, lines);
    }

    private static async Task<List<string>> CaptureAsync(
        SlashCommandDispatcher dispatcher, IConfigStore config, string input, List<string> lines)
    {
        SlashCommandOutcome outcome = await dispatcher.HandleCoreAsync(
            input,
            writer: lines.Add,
            reader: _ => Task.FromResult(string.Empty),
            // The five delegated commands read none of these on the branches
            // under test, and the dispatcher's own harness in
            // SlashCommandFailureSurfacesTests passes null for them too. A null
            // agent is not a special case: it is what a host without an
            // initialised agent hands over, and `ModelCommand` prints
            // "(no active session …)" rather than throwing.
            agent: null!, agentRegistry: new FakeAgentRegistry(),
            configStore: config, authStore: new AuthStore(config),
            providers: new OfflineProviderRegistry(),
            session: Session.Create("/harbor-650", "code", "test-provider", "test-model"));

        await Assert.That(outcome.ShouldQuit).IsFalse()
            .Because("Every command here is a command error at worst — none of them may end the REPL.");

        return lines;
    }

    // ── fakes ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     In-memory config store, so no test writes the developer's real
    ///     <c>~/.harbor/config.json</c> — the reason the #603 fakes exist, and
    ///     the reason this one exposes <see cref="Current" /> for assertions.
    /// </summary>
    private sealed class InMemoryConfigStore : IConfigStore
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
            => Task.FromResult(
                Current.ApiKeys.TryGetValue(providerId, out string? key) && key is not null
                    ? Result.Success(key)
                    : Result.Failure<string>($"No API key for '{providerId}'."));
    }

    /// <summary>Counts the persistence attempts a rule edit actually made.</summary>
    private sealed class RecordingPermissionService : IPermissionService
    {
        public int Saves { get; private set; }

        public Task<Result> SaveAsync(CancellationToken ct = default)
        {
            Saves++;
            return Task.FromResult(Result.Success());
        }

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args,
            CancellationToken ct = default, string? invocationId = null, int generation = 1)
            => throw new NotSupportedException("Not exercised by the argument-passing tests.");

        public Task<Result<PermissionResponse>> AskUserAsync(PermissionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not exercised by the argument-passing tests.");
    }

    /// <summary>
    ///     Provider registry that knows no providers and reaches no network.
    ///     <c>ModelCommand</c> asks it for the cached catalog only to
    ///     disambiguate an unregistered <c>provider/</c> prefix; an empty answer
    ///     sends it down the "this is an explicit provider/model pair" branch,
    ///     which is the one under test.
    /// </summary>
    private sealed class OfflineProviderRegistry : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [];

        public Result<ILlmClient> GetClient(ProviderId providerId)
            => Result.Failure<ILlmClient>($"Provider '{providerId}' is not registered.");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public void Register(ProviderId providerId, Func<ILlmClient> factory)
        {
        }

        public Result Unregister(ProviderId providerId)
            => Result.Failure("OfflineProviderRegistry does not support unregister.");
    }
}
