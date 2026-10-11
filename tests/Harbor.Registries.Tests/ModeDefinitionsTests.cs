using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Registries.Agents;
using TUnit.Assertions;

namespace Harbor.Registries.Tests;

// ModeDefinitionsTests.cs — PX5 slice 1 (issue #1251): mode definitions.
//
// A mode is an agent: permission preset + prompt bias on the record, tool
// allowlist derived by ResolveTools. These tests pin the four new modes'
// presets and carry the slice's end-to-end proof for the one fully wired
// mode — ask is read-only (write-class Deny on every probe path, no mutating
// shell verb Allowed).
public class ModeDefinitionsTests
{
    private static readonly string[] ProbePaths = ["src/Program.cs", "docs/notes.md"];

    private static AgentDefinition[] AllModes() =>
    [
        AgentDefinition.CodeDefault("model", "provider"),
        AgentDefinition.PlanDefault("model", "provider"),
        AgentDefinition.ExploreDefault("model", "provider"),
        AgentDefinition.AskDefault("model", "provider"),
        AgentDefinition.ArchitectDefault("model", "provider"),
        AgentDefinition.DebugDefault("model", "provider"),
        AgentDefinition.OrchestratorDefault("model", "provider"),
    ];

    [Test]
    public async Task EveryMode_HasPromptBias()
    {
        var missing = new List<string>();
        foreach (AgentDefinition mode in AllModes())
        {
            if (mode.Name.Value == "code") continue;
            if (string.IsNullOrWhiteSpace(mode.SystemPromptAppend))
                missing.Add(mode.Name.Value);
        }

        await Assert.That(missing).IsEmpty()
            .Because("the prompt bias is one of the three mode axes; a mode without it is a permission set with no persona (`code` is the bias-less default and is exempt)");
    }

    [Test]
    public async Task EveryMode_RegistersAndResolvesByName()
    {
        var registry = new AgentRegistry();
        foreach (AgentDefinition mode in AllModes())
            registry.Register(mode);

        await Assert.That(registry.GetAllAgents().Count).IsEqualTo(7);

        foreach (AgentDefinition mode in AllModes())
        {
            var resolved = registry.GetAgent(mode.Name);
            await Assert.That(resolved.IsSuccess).IsTrue();
            await Assert.That(resolved.Value.Name).IsEqualTo(mode.Name);
        }
    }

    [Test]
    public async Task Ask_DeniesEveryWriteClassTool_OnEveryProbePath()
    {
        AgentDefinition ask = AgentDefinition.AskDefault("model", "provider");
        string[] writeClass = ["write", "edit", "patch", "notebook", "session_steer"];

        var offenders = new List<string>();
        foreach (string tool in writeClass)
        {
            foreach (string path in ProbePaths)
            {
                PermissionAction action = ask.Permission.Evaluate(tool, path);
                if (action != PermissionAction.Deny)
                    offenders.Add($"{tool} / {path} → {action}");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("ask documents itself as unable to modify files; anything weaker than Deny is a prompt for an answer it was told it could not have");
    }

    [Test]
    public async Task Ask_AllowsNoMutatingShellVerb()
    {
        AgentDefinition ask = AgentDefinition.AskDefault("model", "provider");
        string[] mutating =
        [
            "git commit -m x",
            "git push origin main",
            "git push --force",
            "git reset --hard HEAD~5",
            "git checkout .",
            "git clean -fdx",
        ];

        var allowed = new List<string>();
        foreach (string command in mutating)
        {
            if (ask.Permission.Evaluate("bash", command) == PermissionAction.Allow)
                allowed.Add(command);
        }

        await Assert.That(allowed).IsEmpty()
            .Because("a mutating shell verb reaching Allow in a read-only mode is the hole #798 missed; the dispatcher resolves tools against the full registry, so the ruleset is the boundary");
    }

    [Test]
    public async Task Ask_AllowsEverydayReads()
    {
        AgentDefinition ask = AgentDefinition.AskDefault("model", "provider");

        foreach (string tool in new[] { "read", "glob", "grep", "ls", "tree", "ripgrep", "lsp" })
        {
            await Assert.That(ask.Permission.Evaluate(tool, "src/Program.cs")).IsEqualTo(PermissionAction.Allow)
                .Because($"{tool} is how a question-answering mode sees the codebase");
        }

        await Assert.That(ask.Permission.Evaluate("bash", "git status")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(ask.Permission.Evaluate("bash", "git log --oneline")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task Ask_AndArchitect_BashAllows_AreSubsetOfCodeAllows()
    {
        var baseline = PermissionRuleset.Default.Rules
            .Where(rule => rule.Action == PermissionAction.Allow
                           && rule.Permission.Equals("bash", StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.Pattern)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (AgentDefinition mode in new[] { AgentDefinition.AskDefault("m", "p"), AgentDefinition.ArchitectDefault("m", "p") })
        {
            foreach (PermissionRule rule in mode.Permission.Rules)
            {
                if (rule.Action == PermissionAction.Allow
                    && rule.Permission.Equals("bash", StringComparison.OrdinalIgnoreCase)
                    && !baseline.Contains(rule.Pattern))
                {
                    offenders.Add($"{mode.Name.Value} allows bash \"{rule.Pattern}\"");
                }
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because("a read-only mode may not reach a shell verb the full-access code mode is not itself allowed unprompted");
    }

    [Test]
    public async Task Debug_RunsDiagnostics_WithApprovalForWrites()
    {
        AgentDefinition debug = AgentDefinition.DebugDefault("model", "provider");

        await Assert.That(debug.Permission.Evaluate("bash", "dotnet test foo")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(debug.Permission.Evaluate("bash", "dotnet build foo")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(debug.Permission.Evaluate("bash", "git status")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(debug.Permission.Evaluate("edit", "src/Program.cs")).IsEqualTo(PermissionAction.Ask);
        await Assert.That(debug.Permission.Evaluate("write", "src/Program.cs")).IsEqualTo(PermissionAction.Ask);
        await Assert.That(debug.Permission.Evaluate("bash", "rm -rf /")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Orchestrator_DelegatesWithoutDirectWrites()
    {
        AgentDefinition orchestrator = AgentDefinition.OrchestratorDefault("model", "provider");

        await Assert.That(orchestrator.Permission.Evaluate("task", "*")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(orchestrator.Permission.Evaluate("session_read", "*")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(orchestrator.Permission.Evaluate("session_steer", "*")).IsEqualTo(PermissionAction.Deny)
            .Because("session_steer declares ToolCategory.Write, so the write-category Deny shadows any narrower Ask on tie-sort; the orchestrator delegates through task, never by steering live sessions");
        await Assert.That(orchestrator.Permission.Evaluate("edit", "src/Program.cs")).IsEqualTo(PermissionAction.Deny);
        await Assert.That(orchestrator.Permission.Evaluate("write", "src/Program.cs")).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Ask_PromptBias_NamesAskMode()
    {
        AgentDefinition ask = AgentDefinition.AskDefault("model", "provider");

        await Assert.That(ask.Name).IsEqualTo(AgentName.Create("ask"));
        await Assert.That(ask.Permission.Evaluate("write", "src/Program.cs")).IsEqualTo(PermissionAction.Deny);
        await Assert.That(ask.SystemPromptAppend!.Contains("ASK")).IsTrue()
            .Because("the bias is what tells the model it is answering, not implementing");
    }
}
