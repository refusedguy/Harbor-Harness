using System.Text.Json;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="SkillTool" /> — argument validation, name safety,
///     project-over-global shadowing, legacy flat files, scope filtering and
///     truncation. Skills roots are pinned temp directories (no session store).
/// </summary>
public class SkillToolTests
{
    private string _root = string.Empty;
    private string ProjectSkills => Path.Combine(_root, "project", ".harbor", "skills");
    private string GlobalSkills => Path.Combine(_root, "global", ".harbor", "skills");

    [Before(Test)]
    public void CreateTempRoot()
    {
        _root = Path.Combine(
            Path.GetTempPath(), $"harbor-skill-tool-tests-{Guid.NewGuid():N}");
    }

    [After(Test)]
    public void DeleteTempRoot()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task Name_IsSkill()
    {
        var tool = NewTool();
        await Assert.That(tool.Name.Value).IsEqualTo("skill");
    }

    [Test]
    public async Task ExecutionMode_IsParallel()
    {
        var tool = NewTool();
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
    }

    [Test]
    [Arguments("""{"scope":"any"}""", false, "name")]
    [Arguments("""{"name":"x","scope":"everywhere"}""", false, "scope")]
    [Arguments("""{"name":"x","scope":"project"}""", true, null)]
    public async Task ValidateArguments_Theory(string json, bool expectSuccess, string? expectedErrorSubstring = null)
    {
        var tool = NewTool();
        var args = JsonDocument.Parse(json).RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsSuccess).IsEqualTo(expectSuccess);
        if (expectedErrorSubstring is not null)
            await Assert.That(result.Error).Contains(expectedErrorSubstring);
    }

    [Test]
    public async Task ExecuteAsync_SkillDirectoryLoadsPreferredOverFlatFile()
    {
        WriteSkill(ProjectSkills, "review", "---\ndescription: Project review\n---\n# Project review body\n");
        File.WriteAllText(Path.Combine(ProjectSkills, "review.md"), "# Legacy flat body\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("review"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("Project review body");
    }

    [Test]
    public async Task ExecuteAsync_ProjectShadowsGlobal_OnNameCollision()
    {
        WriteSkill(ProjectSkills, "deploy", "# Project deploy\n");
        WriteSkill(GlobalSkills, "deploy", "# Global deploy\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("deploy"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("Project deploy");
    }

    [Test]
    public async Task ExecuteAsync_GlobalUsed_WhenProjectMissing()
    {
        WriteSkill(GlobalSkills, "deploy", "# Global deploy\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("deploy"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("Global deploy");
    }

    [Test]
    public async Task ExecuteAsync_ScopeGlobal_SkipsProject()
    {
        WriteSkill(ProjectSkills, "deploy", "# Project deploy\n");
        WriteSkill(GlobalSkills, "deploy", "# Global deploy\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("deploy", "global"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("Global deploy");
    }

    [Test]
    public async Task ExecuteAsync_MissingSkill_ReturnsHelpfulError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("nope"), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("nope");
        await Assert.That(result.Output).Contains("available_skills");
    }

    [Test]
    public async Task ExecuteAsync_UnsafeName_Rejected()
    {
        var tool = NewTool();
        foreach (string bad in new[] { "../evil", "a/b", "..", "a\\b" })
        {
            var result = await tool.ExecuteAsync(Args(bad), TestToolContext.Create());
            await Assert.That(result).HasFailed();
        }
    }

    [Test]
    public async Task ExecuteAsync_LongBody_TruncatedWithNote()
    {
        WriteSkill(ProjectSkills, "big", new string('x', SkillTool.MaxContentChars + 100));

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("big"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("truncated");
        await Assert.That(result.Output.Length).IsLessThanOrEqualTo(SkillTool.MaxContentChars + 256);
    }

    // ── the invocation policy gate (#1735) ─────────────────────────────────

    [Test]
    public async Task ExecuteAsync_PolicyBlockedSkill_IsRefusedWithTheReason()
    {
        WriteSkill(ProjectSkills, "deploy", "# Project deploy\n");

        var tool = NewTool(new StubRegistry(blocked: true));
        var result = await tool.ExecuteAsync(Args("deploy"), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).Contains("not invocable by the model");
    }

    [Test]
    public async Task ExecuteAsync_TheGateAllowsTheSkill_TheToolLoadsIt()
    {
        WriteSkill(ProjectSkills, "deploy", "# Project deploy\n");

        var tool = NewTool(new StubRegistry(blocked: false));
        var result = await tool.ExecuteAsync(Args("deploy"), TestToolContext.Create());

        await Assert.That(result).HasSucceeded();
        await Assert.That(result.Output).Contains("Project deploy");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private SkillTool NewTool() =>
        new(null, NullLogger<SkillTool>.Instance, ProjectSkills, GlobalSkills);

    private SkillTool NewTool(ISkillRegistry registry) =>
        new(null, NullLogger<SkillTool>.Instance, ProjectSkills, GlobalSkills, registry);

    private static void WriteSkill(string root, string name, string body)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
    }

    private static JsonElement Args(string name, string? scope = null) =>
        JsonDocument.Parse(scope is null
            ? $$"""{"name":"{{name}}"}"""
            : $$"""{"name":"{{name}}","scope":"{{scope}}"}""").RootElement;

    /// <summary>
    ///     A registry that answers the policy question and nothing else: it
    ///     refuses or admits the named skill. The tool's contract is that it
    ///     asks BEFORE it loads, so the refusal never depends on the file.
    /// </summary>
    private sealed class StubRegistry(bool blocked) : ISkillRegistry
    {
        public IReadOnlyList<SkillDescriptor> GetSkills(
            string workingDirectory,
            SkillCaller caller,
            string? mode = null,
            string? category = null)
        {
            if (blocked)
            {
                return Array.Empty<SkillDescriptor>();
            }

            return [Skill("deploy")];
        }

        public CSharpFunctionalExtensions.Result<SkillDescriptor> ResolveForCall(
            string workingDirectory,
            string name,
            SkillCaller caller,
            string? mode = null,
            string? category = null)
            => blocked
                ? CSharpFunctionalExtensions.Result.Failure<SkillDescriptor>(
                    $"Skill '{name}' is not invocable by the model: its invocation policy admits the human only.")
                : CSharpFunctionalExtensions.Result.Success(Skill(name));

        private static SkillDescriptor Skill(string name) =>
            new(name, "a skill", $"/skills/{name}.md");
    }
}
