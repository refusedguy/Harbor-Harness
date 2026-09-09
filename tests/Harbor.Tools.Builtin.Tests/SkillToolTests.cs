using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for <see cref="SkillTool" /> — argument validation, name safety,
///     project-over-global shadowing, legacy flat files, scope filtering and
///     truncation. Skills roots are pinned temp directories (no session store).
/// </summary>
public class SkillToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"harbor-skill-tool-tests-{Guid.NewGuid():N}");
    private string ProjectSkills => Path.Combine(_root, "project", ".harbor", "skills");
    private string GlobalSkills => Path.Combine(_root, "global", ".harbor", "skills");

    public void Dispose()
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
        var result = await tool.ExecuteAsync(Args("review"), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("Project review body");
    }

    [Test]
    public async Task ExecuteAsync_ProjectShadowsGlobal_OnNameCollision()
    {
        WriteSkill(ProjectSkills, "deploy", "# Project deploy\n");
        WriteSkill(GlobalSkills, "deploy", "# Global deploy\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("deploy"), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("Project deploy");
    }

    [Test]
    public async Task ExecuteAsync_GlobalUsed_WhenProjectMissing()
    {
        WriteSkill(GlobalSkills, "deploy", "# Global deploy\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("deploy"), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("Global deploy");
    }

    [Test]
    public async Task ExecuteAsync_ScopeGlobal_SkipsProject()
    {
        WriteSkill(ProjectSkills, "deploy", "# Project deploy\n");
        WriteSkill(GlobalSkills, "deploy", "# Global deploy\n");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("deploy", "global"), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("Global deploy");
    }

    [Test]
    public async Task ExecuteAsync_MissingSkill_ReturnsHelpfulError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("nope"), CreateContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("nope");
        await Assert.That(result.Output).Contains("available_skills");
    }

    [Test]
    public async Task ExecuteAsync_UnsafeName_Rejected()
    {
        var tool = NewTool();
        foreach (string bad in new[] { "../evil", "a/b", "..", "a\\b" })
        {
            var result = await tool.ExecuteAsync(Args(bad), CreateContext());
            await Assert.That(result.IsError).IsTrue();
        }
    }

    [Test]
    public async Task ExecuteAsync_LongBody_TruncatedWithNote()
    {
        WriteSkill(ProjectSkills, "big", new string('x', SkillTool.MaxContentChars + 100));

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("big"), CreateContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("truncated");
        await Assert.That(result.Output.Length).IsLessThanOrEqualTo(SkillTool.MaxContentChars + 256);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private SkillTool NewTool() =>
        new(null, NullLogger<SkillTool>.Instance, ProjectSkills, GlobalSkills);

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

    private static ToolContext CreateContext() => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);
}
