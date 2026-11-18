using System.Runtime.CompilerServices;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using FakePermissionServiceBase = Harbor.TestKit.Generated.FakePermissionService;

namespace Harbor.Application.Tests;

/// <summary>
///     #1100 — allocation gate and behavior pins for the per-tool-call
///     <c>ToolDispatcher.ResolveTool</c>. The old
///     <c>TryCreate().MapError(...).Bind(name =&gt; GetTool(name).MapError(...))</c>
///     chain built two capturing display-class + delegate pairs on every
///     tool call of every turn. The imperative form keeps both failure texts
///     and the same success instance while allocating only the one
///     <see cref="ToolName"/> record the parse already needed.
/// </summary>
public class ToolDispatcherResolveToolAllocationTests
{
    private const int Iterations = 4096;

    /// <summary>
    ///     One <see cref="ToolName"/> record is 32 B on 64-bit (24 B header +
    ///     one string reference). 96 B leaves headroom for a different object
    ///     header without ever admitting the old closure pair (~200 B/call).
    /// </summary>
    private const long ThresholdBytesPerCall = 96;

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

    private sealed class NoopTool : ITool
    {
        public ToolName Name { get; } = ToolName.Create("noop");
        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
        public string DisplayName => "noop";
        public string Description => "ResolveTool gate harness tool.";
        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => [];
        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object"}""");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken ct = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    /// <summary>
    ///     Minimal registry: GetTool returns a cached success (a struct — no
    ///     allocation), so the measured bytes come from ResolveTool alone.
    /// </summary>
    private sealed class StubRegistry(ITool tool, bool known = true) : IToolRegistry
    {
        private readonly IReadOnlyList<ToolDescriptor> _all =
            [new ToolDescriptor(tool.Name, tool.DisplayName, tool.Description,
                tool.ParameterSchema, tool.ExecutionMode, tool.PromptSnippet, tool.PromptGuidelines)];

        public IReadOnlyList<ToolDescriptor> GetAllTools() => _all;

        public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) => _all;

        public IReadOnlyList<IArgSafetyPolicy> SafetyPolicies => ToolSafetyPolicies.Build([]);

        public Result<ITool> GetTool(ToolName name) =>
            known ? Result.Success(tool) : Result.Failure<ITool>($"Unknown tool '{name.Value}'.");

        public Result Register(ITool value) => Result.Success();

        public Result Unregister(ToolName name) => Result.Success();
    }

    private static ToolDispatcher NewDispatcher(IToolRegistry registry) =>
        new(registry, new AllowPermissions(), new FakeEventBus(), NullLogger<ToolDispatcher>.Instance);

    /// <summary>
    ///     The removed chain, kept ONLY as a sensitivity control: the gate
    ///     must fail on the shape this slice replaced, or it proves nothing.
    ///     Static so the two captured values (registry, raw name) are exactly
    ///     the shape the old instance method captured through <c>this</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result<ITool> OldResolveChain(IToolRegistry tools, string rawName) =>
        ToolName.TryCreate(rawName)
            .MapError(e => $"Invalid tool name: {e}")
            .Bind(name => tools.GetTool(name).MapError(_ => $"Unknown tool: '{rawName}'."));

    private static double AllocatedPerCall(Func<Result<ITool>> resolve)
    {
        for (int i = 0; i < Iterations; i++) _ = resolve();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Result<ITool> sink = default;
        for (int i = 0; i < Iterations; i++) sink = resolve();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(sink);

        return (double)allocated / Iterations;
    }

    [Test]
    public async Task ResolveTool_ValidName_ReturnsTheRegisteredInstance()
    {
        var tool = new NoopTool();
        var dispatcher = NewDispatcher(new StubRegistry(tool));

        Result<ITool> resolved = dispatcher.ResolveTool("noop");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(ReferenceEquals(resolved.Value, tool)).IsTrue();
    }

    [Test]
    public async Task ResolveTool_InvalidName_ErrorTextIsUnchanged()
    {
        const string raw = "Bad Name!";
        var dispatcher = NewDispatcher(new StubRegistry(new NoopTool()));

        Result<ITool> resolved = dispatcher.ResolveTool(raw);

        // The exact old-railway text, computed from the same parse failure.
        string expected = ToolName.TryCreate(raw)
            .MapError(e => $"Invalid tool name: {e}")
            .Error;

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).IsEqualTo(expected);
        await Assert.That(expected).Contains("Invalid tool name:");
    }

    [Test]
    public async Task ResolveTool_EmptyName_ErrorTextIsUnchanged()
    {
        var dispatcher = NewDispatcher(new StubRegistry(new NoopTool()));

        Result<ITool> resolved = dispatcher.ResolveTool("");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).IsEqualTo("Invalid tool name: Tool name cannot be empty");
    }

    [Test]
    public async Task ResolveTool_UnknownTool_UsesAvailableToolsDiagnostic()
    {
        var dispatcher = NewDispatcher(new StubRegistry(new NoopTool(), known: false));

        Result<ITool> resolved = dispatcher.ResolveTool("noop");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).IsEqualTo("Unknown tool: 'noop'. Available: noop");
    }

    [Test]
    [NotInParallel]
    public async Task ResolveTool_SuccessPath_DropsTheCapturingClosures()
    {
        var tool = new NoopTool();
        var registry = new StubRegistry(tool);
        var dispatcher = NewDispatcher(registry);

        // The delegate is built once outside the window; invoking it does not
        // allocate. GetTool returns a cached Result<T> struct.
        Func<Result<ITool>> current = () => dispatcher.ResolveTool("noop");
        Func<Result<ITool>> old = () => OldResolveChain(registry, "noop");

        double currentBytes = AllocatedPerCall(current);
        double oldBytes = AllocatedPerCall(old);

        await Assert.That(currentBytes).IsLessThan(ThresholdBytesPerCall).Because(
            "ResolveTool must allocate only the one ToolName record; the old chain built two closure pairs");
        // Sensitivity: the gate fails on the code this slice removed.
        await Assert.That(oldBytes).IsGreaterThan(currentBytes * 1.5).Because(
            "the control proves the gate would fail on the chained TryCreate/Bind shape");
    }
}
