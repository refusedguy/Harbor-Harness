using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Agents;
using Harbor.Application.Agents.Pipeline;
using Harbor.Application.Hooks;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.Diagnostics;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakePermissionServiceBase = Harbor.TestKit.Generated.FakePermissionService; // #1769: generated double

using TestSessionContext = Harbor.TestKit.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     PX4 slice 1: user hooks (plain shell commands from hooks.json — not
///     plugins). Runner verdicts, fail-closed gates, and the three event
///     points (PreToolUse, PostToolUse, SessionEnd).
/// </summary>
public class HookRunnerTests
{
    private sealed class AllowPermissions : FakePermissionServiceBase
    {
        public AllowPermissions()
        {
            CheckAsyncResult = Result.Success(TestToolContext.AllowWithoutPersist());
            AskUserAsyncResult = Result.Success(TestToolContext.DenyWithoutPersist());
            GetRulesetValue = PermissionRuleset.Empty;
            SaveAsyncResult = Result.Success();
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harbor-hooks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HookRunner NewRunner(string dir, string hooksJson) =>
        NewRunnerAt(Path.Combine(dir, "hooks.json"), hooksJson);

    private static HookRunner NewRunnerAt(string path, string hooksJson)
    {
        File.WriteAllText(path, hooksJson);
        return new HookRunner(path, NullLogger<HookRunner>.Instance, TimeSpan.FromSeconds(10));
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "hooks harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"));

    private static ToolCallPart CounterCall() => new(
        "tc1", "counter", JsonDocument.Parse("""{"n":1}""").RootElement);

    // ── Matcher ──────────────────────────────────────────────────────────

    [Test]
    public async Task Matcher_NullOrWildcard_MatchesAll()
    {
        await Assert.That(HookConfig.Matches(null, "bash")).IsTrue();
        await Assert.That(HookConfig.Matches("", "bash")).IsTrue();
        await Assert.That(HookConfig.Matches("*", "bash")).IsTrue();
    }

    [Test]
    public async Task Matcher_PipeList_MatchesCaseInsensitively()
    {
        await Assert.That(HookConfig.Matches("bash|write", "Bash")).IsTrue();
        await Assert.That(HookConfig.Matches("bash|write", "WRITE")).IsTrue();
        await Assert.That(HookConfig.Matches("bash|write", "read")).IsFalse();
    }

    // ── PreToolUse verdicts ──────────────────────────────────────────────

    [Test]
    public async Task PreToolUse_NoConfigFile_Allows()
    {
        var runner = new HookRunner(
            Path.Combine(NewTempDir(), "hooks.json"), NullLogger<HookRunner>.Instance);
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
        await Assert.That(verdict.EditedArgs).IsNull();
    }

    [Test]
    public async Task PreToolUse_DenyHook_ReturnsDenyWithReason()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"deny\",\"reason\":\"nope\"}'"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
        await Assert.That(verdict.Reason!).Contains("nope");
    }

    [Test]
    public async Task PreToolUse_SilentSuccess_Allows()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"true"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
    }

    [Test]
    public async Task PreToolUse_SilentNonZeroExit_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"exit 3"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task PreToolUse_InvalidVerdictJson_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"echo not-json"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task PreToolUse_UnknownDecision_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"echo '{\"decision\":\"maybe\"}'"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task PreToolUse_Timeout_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"sleep 30","timeout":1}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
        await Assert.That(verdict.Reason!).Contains("timed out");
    }

    [Test]
    public async Task PreToolUse_EditHook_ReturnsEditedArgs()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"edit\",\"editedArgs\":{\"n\":2}}'"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
        await Assert.That(verdict.EditedArgs).IsNotNull();
        await Assert.That(verdict.EditedArgs!.Value.GetRawText()).Contains("2");
    }

    [Test]
    public async Task PreToolUse_NonMatchingMatcher_SkipsHook()
    {
        // The command would deny if it ran — allow proves it never spawned.
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"other","command":"exit 3"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
    }

    [Test]
    public async Task PreToolUse_CorruptConfig_AllowsButRecordsLoadError()
    {
        string dir = NewTempDir();
        var runner = NewRunner(dir, """{not json""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
        var config = new HookConfig(Path.Combine(dir, "hooks.json"));
        _ = config.GetHooks(HookEvents.PreToolUse, "counter");
        await Assert.That(config.LoadError).IsNotNull();
    }

    // ── Advisory points ──────────────────────────────────────────────────

    [Test]
    public async Task PostToolUse_RunsCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "post-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"PostToolUse\":[{\"command\":\"touch " + marker + "\"}]}}");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        await runner.RunPostToolUseAsync("counter", doc.RootElement, ToolResult.Success("ok"), "s1");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task PostToolUse_FailingHook_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PostToolUse":[{"command":"exit 3"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        await runner.RunPostToolUseAsync("counter", doc.RootElement, ToolResult.Success("ok"), "s1");
    }

    [Test]
    public async Task SessionEnd_RunsCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "end-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"SessionEnd\":[{\"command\":\"touch " + marker + "\"}]}}");

        await runner.RunSessionEndAsync("s1");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    // ── Dispatcher integration ───────────────────────────────────────────

    private static ToolDispatcher NewDispatcher(IHookRunner? hooks, CountingTool tool) =>
        new(new FakeToolRegistry(tool), new AllowPermissions(), new FakeEventBus(),
            NullLogger<ToolDispatcher>.Instance, null, null, null, null, hooks);

    [Test]
    public async Task Dispatcher_PreToolUseDeny_ToolNeverExecutes()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var hooks = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"deny\",\"reason\":\"hooked\"}'"}]}}""");
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(hooks, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(message.Results.Count).IsEqualTo(1);
        await Assert.That(message.Results[0]).HasFailed();
        await Assert.That(message.Results[0].Output).Contains("hooked");
    }

    [Test]
    public async Task Dispatcher_PreToolUseEdit_ToolSeesEditedArgs()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var hooks = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"edit\",\"editedArgs\":{\"n\":2}}'"}]}}""");
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(hooks, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0]).HasSucceeded();
        await Assert.That(tool.ExecutedArgs.Count).IsEqualTo(1);
        await Assert.That(tool.ExecutedArgs[0]).Contains("2");
    }

    [Test]
    public async Task Dispatcher_PostToolUse_RunsAfterExecution()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "post-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"PostToolUse\":[{\"command\":\"touch " + marker + "\"}]}}");
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(hooks, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0]).HasSucceeded();
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task Dispatcher_NoHooks_LegacyPathStillExecutes()
    {
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(null, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0]).HasSucceeded();
    }

    // ── Loop integration (SessionEnd) ────────────────────────────────────

    [Test]
    public async Task Loop_SessionEndHook_RunsAtEnd()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "end-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"SessionEnd\":[{\"command\":\"touch " + marker + "\"}]}}");
        AgentDefinition agent = CodeAgent();
        var agents = new FakeAgentRegistry(agent);
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                TestEvents.TextDelta(delta: "done"),
                TestEvents.StepFinish(usage: new Usage(1, 1))
            }
        ]);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(new FakeAgentRegistry(agent), NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            hookRunner: hooks);

        Result result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result).IsSuccessful();
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    // ── Slice 2: SessionStart ─────────────────────────────────────────────

    [Test]
    public async Task SessionStart_RunsCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "start-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"SessionStart\":[{\"command\":\"touch " + marker + "\"}]}}");

        await runner.RunSessionStartAsync("s1", "code", "test-model");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task SessionStart_FailingHook_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"SessionStart":[{"command":"exit 3"}]}}""");

        await runner.RunSessionStartAsync("s1", "code", "test-model");
    }

    [Test]
    public async Task SessionStart_NoToolToMatch_EntryStillRuns()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A session event has no tool, so a tool-shaped matcher is not a
        // filter for it: the entry is a "run when a run starts" declaration.
        // Pinned because the alternative — silently never firing — is
        // indistinguishable from a missing hook.
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "start-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"SessionStart\":[{\"matcher\":\"bash\",\"command\":\"touch " + marker + "\"}]}}");

        await runner.RunSessionStartAsync("s1");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task SessionStart_PayloadCarriesAgentAndModel()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string captured = Path.Combine(dir, "start-payload.json");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"SessionStart\":[{\"command\":\"cat > " + captured + "\"}]}}");

        await runner.RunSessionStartAsync("s1", "code", "test-model");

        string json = File.ReadAllText(captured);
        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("event").GetString()).IsEqualTo("SessionStart");
        await Assert.That(doc.RootElement.GetProperty("sessionId").GetString()).IsEqualTo("s1");
        await Assert.That(doc.RootElement.GetProperty("agent").GetString()).IsEqualTo("code");
        await Assert.That(doc.RootElement.GetProperty("model").GetString()).IsEqualTo("test-model");
        // Slice-1 fields stay present and null: a hook written against slice 1
        // reads the same shape it always did.
        await Assert.That(doc.RootElement.GetProperty("tool").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(doc.RootElement.GetProperty("prompt").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    // ── Slice 2: UserPromptSubmit ─────────────────────────────────────────

    [Test]
    public async Task UserPromptSubmit_DenyHook_ReturnsDenyWithReason()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"UserPromptSubmit":[{"command":"echo '{\"decision\":\"deny\",\"reason\":\"no\"}'"}]}}""");

        HookVerdict verdict = await runner.RunUserPromptSubmitAsync("s1", "delete everything", "code", "test-model");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
        await Assert.That(verdict.Reason!).Contains("no");
    }

    [Test]
    public async Task UserPromptSubmit_SilentSuccess_Allows()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"UserPromptSubmit":[{"command":"true"}]}}""");

        HookVerdict verdict = await runner.RunUserPromptSubmitAsync("s1", "hello", "code", "test-model");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
    }

    [Test]
    public async Task UserPromptSubmit_InvalidVerdictJson_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"UserPromptSubmit":[{"command":"echo not-json"}]}}""");

        HookVerdict verdict = await runner.RunUserPromptSubmitAsync("s1", "hello", "code", "test-model");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task UserPromptSubmit_EditVerdict_ReadsAsAllow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A prompt has no args to edit. Answering `edit` means "I reviewed it
        // and it may proceed", which is allow — NOT the fail-closed deny that
        // an arg-less `edit` gets at PreToolUse.
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"UserPromptSubmit":[{"command":"echo '{\"decision\":\"edit\"}'"}]}}""");

        HookVerdict verdict = await runner.RunUserPromptSubmitAsync("s1", "hello", "code", "test-model");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
    }

    [Test]
    public async Task UserPromptSubmit_PayloadCarriesPrompt()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string captured = Path.Combine(dir, "prompt-payload.json");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"UserPromptSubmit\":[{\"command\":\"cat > " + captured + "\"}]}}");

        await runner.RunUserPromptSubmitAsync("s1", "delete /tmp/x", "code", "test-model");

        using var doc = JsonDocument.Parse(File.ReadAllText(captured));
        await Assert.That(doc.RootElement.GetProperty("event").GetString()).IsEqualTo("UserPromptSubmit");
        await Assert.That(doc.RootElement.GetProperty("prompt").GetString()).IsEqualTo("delete /tmp/x");
        await Assert.That(doc.RootElement.GetProperty("agent").GetString()).IsEqualTo("code");
        await Assert.That(doc.RootElement.GetProperty("model").GetString()).IsEqualTo("test-model");
    }

    // ── Slice 2: PreCompact ──────────────────────────────────────────────

    [Test]
    public async Task PreCompact_RunsCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "precompact-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"PreCompact\":[{\"command\":\"touch " + marker + "\"}]}}");

        await runner.RunPreCompactAsync("s1", "code", "test-model");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task PreCompact_FailingHook_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreCompact":[{"command":"exit 3"}]}}""");

        await runner.RunPreCompactAsync("s1", "code", "test-model");
    }

    // ── Slice 2: loop integration ────────────────────────────────────────

    /// <summary>
    ///     Build a loop wired to <paramref name="hooks" /> whose single
    ///     scripted turn is one user prompt → one stop. Used for the run-start
    ///     events, which fire once before the first turn.
    /// </summary>
    private static (AgentLoop Loop, ScriptedLlmClient Client) NewHookedLoop(IHookRunner? hooks)
    {
        var client = new ScriptedLlmClient(
        [
            TestEvents.TextDelta(delta: "done"),
            TestEvents.StepFinish(usage: new Usage(1, 1))
        ]);
        var agent = CodeAgent();
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            new FakeAgentRegistry(agent),
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(new FakeAgentRegistry(agent), NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            hookRunner: hooks);
        return (loop, client);
    }

    /// <summary>
    ///     A session whose LAST message is a user prompt — the shape
    ///     <c>DefaultAgent.PromptAsync</c> produces (persist, then load and run).
    /// </summary>
    private static TestSessionContext NewSessionWithPrompt(string content) => new(
        Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"),
        new AgentMessage[]
        {
            TestMessages.User(content, sessionId: "session-id")
        });

    [Test]
    public async Task Loop_SessionStartHook_RunsAtRunStart()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "start-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"SessionStart\":[{\"command\":\"touch " + marker + "\"}]}}");
        (AgentLoop loop, _) = NewHookedLoop(hooks);
        AgentDefinition agent = CodeAgent();

        Result result = await loop.RunAsync(NewSessionWithPrompt("hello"), agent)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(result).IsSuccessful();
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task Loop_UserPromptSubmitDeny_ModelNeverCalled()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        var hooks = NewRunner(dir,
            """{"hooks":{"UserPromptSubmit":[{"command":"echo '{\"decision\":\"deny\",\"reason\":\"blocked by hook\"}'"}]}}""");
        (AgentLoop loop, ScriptedLlmClient client) = NewHookedLoop(hooks);
        AgentDefinition agent = CodeAgent();

        Result result = await loop.RunAsync(NewSessionWithPrompt("do the thing"), agent)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        // The gate sits before the first turn, so nothing was streamed.
        await Assert.That(result).IsFailed();
        await Assert.That(result.Error).Contains("blocked by hook");
        await Assert.That(client.StreamCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Loop_UserPromptSubmitAllow_ModelCalled()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        var hooks = NewRunner(dir,
            """{"hooks":{"UserPromptSubmit":[{"command":"true"}]}}""");
        (AgentLoop loop, ScriptedLlmClient client) = NewHookedLoop(hooks);
        AgentDefinition agent = CodeAgent();

        Result result = await loop.RunAsync(NewSessionWithPrompt("do the thing"), agent)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(result).IsSuccessful();
        await Assert.That(client.StreamCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Loop_UserPromptSubmitHookFails_RunDeniedFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A hook that cannot decide must not let the prompt through: this is
        // the whole reason the gate is fail-closed rather than advisory.
        var hooks = NewRunner(NewTempDir(),
            """{"hooks":{"UserPromptSubmit":[{"command":"echo not-json"}]}}""");
        (AgentLoop loop, ScriptedLlmClient client) = NewHookedLoop(hooks);
        AgentDefinition agent = CodeAgent();

        Result result = await loop.RunAsync(NewSessionWithPrompt("do the thing"), agent)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(result).IsFailed();
        await Assert.That(client.StreamCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Loop_NoPromptMessage_PromptGateSkipped()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A run opened without a fresh user message has no prompt to submit.
        // The hook is not fired with a stale one, and the run proceeds.
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "prompt-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"UserPromptSubmit\":[{\"command\":\"touch " + marker + "\"}]}}");
        (AgentLoop loop, ScriptedLlmClient client) = NewHookedLoop(hooks);
        AgentDefinition agent = CodeAgent();
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"));

        Result result = await loop.RunAsync(session, agent)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(result).IsSuccessful();
        await Assert.That(File.Exists(marker)).IsFalse();
        await Assert.That(client.StreamCalls).IsEqualTo(1);
    }

    // ── Slice 2: compaction behavior (PreCompact) ────────────────────────

    [Test]
    public async Task CompactionBehavior_OverThreshold_FiresPreCompactHook()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "precompact-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"PreCompact\":[{\"command\":\"touch " + marker + "\"}]}}");
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"));
        var behavior = new CompactionBehavior(
            new FakeCompactionService(),
            new FakeTokenTracker(shouldCompact: true),
            new FakeEventBus(),
            NullMetrics.Instance,
            NullLogger.Instance,
            hooks);

        CompactionOutcome outcome = await behavior.BeforeTurnAsync(
            session, session.Messages, ScriptedLlmClient.TestModel,
            truncationFallback: false, CancellationToken.None);

        await Assert.That(File.Exists(marker)).IsTrue();
        // Advisory: a failing compaction outcome is unchanged by the hook.
        await Assert.That(outcome.TruncationFallback).IsTrue();
    }

    [Test]
    public async Task CompactionBehavior_UnderThreshold_NoPreCompactHook()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "precompact-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"PreCompact\":[{\"command\":\"touch " + marker + "\"}]}}");
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"));
        var behavior = new CompactionBehavior(
            new FakeCompactionService(),
            new FakeTokenTracker(shouldCompact: false),
            new FakeEventBus(),
            NullMetrics.Instance,
            NullLogger.Instance,
            hooks);

        await behavior.BeforeTurnAsync(
            session, session.Messages, ScriptedLlmClient.TestModel,
            truncationFallback: false, CancellationToken.None);

        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test]
    public async Task CompactionBehavior_NoHookRunner_StillCompacts()
    {
        // The optional seam: a directly-constructed behavior without a runner
        // keeps the slice-1 path exactly.
        var session = new TestSessionContext(
            Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"));
        var compaction = new FakeCompactionService();
        var behavior = new CompactionBehavior(
            compaction,
            new FakeTokenTracker(shouldCompact: true),
            new FakeEventBus(),
            NullMetrics.Instance,
            NullLogger.Instance);

        await behavior.BeforeTurnAsync(
            session, session.Messages, ScriptedLlmClient.TestModel,
            truncationFallback: false, CancellationToken.None);

        await Assert.That(compaction.Calls).IsEqualTo(1);
    }
}
