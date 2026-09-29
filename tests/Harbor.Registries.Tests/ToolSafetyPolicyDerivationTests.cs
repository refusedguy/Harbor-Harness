using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using System.Text.Json;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

/// <summary>
///     Issue #557: the path-traversal guard used to be gated on membership of a
///     hand-maintained name list inside <c>PathGuardSafetyPolicy</c>. A path-taking
///     write tool that was not added to it got <c>AppliesTo() == false</c>, the
///     suppression never ran, and <c>new("mytool", "src/*", Allow)</c> authorised
///     <c>src/../../../etc/passwd</c> — a permission bypass with no test, no warning
///     and no compile error.
/// </summary>
/// <remarks>
///     <para>
///         These tests pin the replacement: the guard set is DERIVED from what
///         tools registered (<see cref="ToolSafetyPolicies" /> over
///         <see cref="ToolRegistry.SafetyPolicies" />). The synthetic tool below is
///         invented here, appears in no list, and is refused anyway — which is the
///         whole claim.
///     </para>
///     <para>
///         They also pin the fail-loud half: a contradictory or blank declaration
///         throws at build time instead of producing a policy set that quietly
///         guards nothing.
///     </para>
/// </remarks>
public class ToolSafetyPolicyDerivationTests
{
    /// <summary>
    ///     A path-taking write tool invented for this test. It exists in no list
    ///     anywhere in the repository — that is the point.
    /// </summary>
    private sealed class SyntheticPathWriteTool : ITool
    {
        public ToolName Name => ToolName.Create("synthetic_unsafe");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Path();

        public string DisplayName => "Synthetic Path Write";

        public string Description => "Writes a file. Invented to prove the guard is derived.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path":    { "type": "string", "description": "File to write" },
                "content": { "type": "string", "description": "File content" }
              },
              "required": ["path", "content"]
            }
            """);

        public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("written"));
    }

    /// <summary>A tool that declares no path argument — the explicit opt-out.</summary>
    private sealed class SyntheticOpaqueTool : ITool
    {
        public ToolName Name => ToolName.Create("synthetic_opaque");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string DisplayName => "Synthetic Opaque";

        public string Description => "Takes a name, not a path.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
            { "type": "object", "properties": { "name": { "type": "string" } } }
            """);

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("ok"));
    }

    private static PermissionRuleset AnchoredSrcAllow() => new(
    [
        new PermissionRule("synthetic_unsafe", "src/*", PermissionAction.Allow),
        new PermissionRule("synthetic_opaque", "src/*", PermissionAction.Allow),
    ]);

    // ── The claim: no list, no bypass ────────────────────────────────────

    [Test]
    public async Task Registered_PathTool_Traversal_IsNotAllowed_WithoutAppearingInAnyList()
    {
        var registry = new ToolRegistry();
        var registered = registry.Register(new SyntheticPathWriteTool());
        await Assert.That(registered.IsSuccess).IsTrue();

        // The ruleset a user writes: "this tool may write under src/".
        var ruleset = AnchoredSrcAllow();

        // Policies come from the REGISTRY — the same source the live
        // IPermissionService reads — not from any static list.
        var guarded = ruleset.Evaluate("synthetic_unsafe", "src/../../../etc/passwd", registry.SafetyPolicies);

        await Assert.That(guarded).IsNotEqualTo(PermissionAction.Allow)
            .Because("a path-taking tool is guarded by being registered; the old "
                     + "PathGuardSafetyPolicy.DefaultTools list would not have contained "
                     + "'synthetic_unsafe' and this would have returned Allow");
    }

    [Test]
    public async Task Registered_PathTool_AbsolutePath_IsNotAllowed()
    {
        var registry = new ToolRegistry();
        registry.Register(new SyntheticPathWriteTool());

        var guarded = AnchoredSrcAllow().Evaluate(
            "synthetic_unsafe", "/etc/passwd", registry.SafetyPolicies);

        await Assert.That(guarded).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Registered_PathTool_PathInsideSrc_IsStillAllowed()
    {
        var registry = new ToolRegistry();
        registry.Register(new SyntheticPathWriteTool());

        var allowed = AnchoredSrcAllow().Evaluate(
            "synthetic_unsafe", "src/Program.cs", registry.SafetyPolicies);

        await Assert.That(allowed).IsEqualTo(PermissionAction.Allow)
            .Because("the guard suppresses only unsafe path SHAPES; an ordinary "
                     + "workspace-relative path must keep working");
    }

    [Test]
    public async Task Registered_PathTool_RegisteredAfterFirstLookup_IsStillGuarded()
    {
        // The registry recomputes on every registration change, so a tool that
        // lands later (a plugin, a hot reload) is guarded from its first check.
        var registry = new ToolRegistry();
        await Assert.That(registry.SafetyPolicies.Count).IsEqualTo(0);

        registry.Register(new SyntheticPathWriteTool());

        var guarded = AnchoredSrcAllow().Evaluate(
            "synthetic_unsafe", "src/../../../etc/passwd", registry.SafetyPolicies);
        await Assert.That(guarded).IsNotEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Guard_Coverage_Follows_The_Registry_Not_The_Ruleset()
    {
        // The honest boundary of the fix: coverage is a property of the registry
        // that owns the tool, not of the ruleset. An EMPTY registry derives an empty
        // policy set, so nothing claims a tool it has never seen. That is why the
        // live IPermissionService always passes the real registry's policies, and
        // why PermissionRuleset.DefaultSafetyPolicies still carries the builtin
        // vocabulary for the two registry-less callers (direct Evaluate, and
        // ResolveTools' "*" probe, where the guard is inert anyway).
        var empty = new ToolRegistry();

        var unguarded = AnchoredSrcAllow().Evaluate("synthetic_unsafe", "src/../x", empty.SafetyPolicies);
        await Assert.That(unguarded).IsEqualTo(PermissionAction.Allow);

        var owning = new ToolRegistry();
        owning.Register(new SyntheticPathWriteTool());
        var guarded = AnchoredSrcAllow().Evaluate("synthetic_unsafe", "src/../x", owning.SafetyPolicies);
        await Assert.That(guarded).IsNotEqualTo(PermissionAction.Allow);
    }

    // ── The opt-out is explicit ───────────────────────────────────────────

    [Test]
    public async Task Tool_Declaring_Opaque_IsNot_PathGuarded()
    {
        var registry = new ToolRegistry();
        registry.Register(new SyntheticOpaqueTool());

        var allowed = AnchoredSrcAllow().Evaluate(
            "synthetic_opaque", "src/../x", registry.SafetyPolicies);

        await Assert.That(allowed).IsEqualTo(PermissionAction.Allow)
            .Because("ToolSafetyProfile.Opaque is a DECLARED opt-out, not a default: "
                     + "a tool that opts out of the path guard is not silently guarded");
    }

    [Test]
    public async Task Tool_Declaring_Command_Gets_TheDenyList_NotThePathGuard()
    {
        var registry = new ToolRegistry();
        registry.Register(new SyntheticShellTool());
        var ruleset = new PermissionRuleset([new PermissionRule("synthetic_shell", "*", PermissionAction.Allow)]);

        await Assert.That(ruleset.Evaluate("synthetic_shell", "rm -rf /", registry.SafetyPolicies))
            .IsEqualTo(PermissionAction.Deny)
            .Because("declaring ToolArgKind.Command routes the tool to "
                     + "BashSafetyPolicy instead of the path guard");

        await Assert.That(ruleset.Evaluate("synthetic_shell", "ls ../secrets", registry.SafetyPolicies))
            .IsEqualTo(PermissionAction.Allow)
            .Because("a command is not a path; the traversal guard must not fire on it");
    }

    private sealed class SyntheticShellTool : ITool
    {
        public ToolName Name => ToolName.Create("synthetic_shell");

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Command();

        public string DisplayName => "Synthetic Shell";

        public string Description => "Runs a command. Invented to prove Command routing.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
            { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] }
            """);

        public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(ToolResult.Success("ok"));
    }

    // ── Fail loud, or the derived set is worthless ────────────────────────

    [Test]
    public async Task Build_BlankToolName_Throws()
    {
        var ex = await Assert.Throws<InvalidOperationException>(() =>
        {
            ToolSafetyPolicies.Build([new ToolSafetyDeclaration("  ", ToolSafetyProfile.Path())]);
        });

        await Assert.That(ex!.Message).Contains("blank name");
    }

    [Test]
    public async Task Build_BlankArgumentName_Throws()
    {
        var ex = await Assert.Throws<InvalidOperationException>(() =>
        {
            ToolSafetyPolicies.Build(
                [new ToolSafetyDeclaration("x", new ToolSafetyProfile(ToolArgKind.Path, " "))]);
        });

        await Assert.That(ex!.Message).Contains("blank name");
    }

    [Test]
    public async Task Build_ConflictingDuplicate_Throws()
    {
        var ex = await Assert.Throws<InvalidOperationException>(() =>
        {
            ToolSafetyPolicies.Build(
            [
                new ToolSafetyDeclaration("dupe", ToolSafetyProfile.Path()),
                new ToolSafetyDeclaration("DUPE", ToolSafetyProfile.Opaque),
            ]);
        });

        await Assert.That(ex!.Message).Contains("conflicting");
    }

    [Test]
    public async Task Build_NullDeclarations_Throws()
    {
        await Assert.Throws<ArgumentNullException>(() =>
        {
            ToolSafetyPolicies.Build(null!);
        });
    }
}
