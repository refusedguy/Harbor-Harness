// SkillPathValidatorWiringTests.cs — issue #3415.
//
// SkillPathValidator used to be a pure leaf with zero product callers: SkillTool
// guarded names with its own ad-hoc IsSafeName check. The tool now admits a
// name only through SkillPathValidator.IsSafeSegment, before any file access.
// These tests drive the real ExecuteAsync path: a '../' name cannot climb out
// of the skills root, and the guard's stricter grammar is the one actually in
// the path (a name the old check accepted is now refused).

using System.Text.Json;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class SkillPathValidatorWiringTests
{
    private string _root = string.Empty;
    private string Skills => Path.Combine(_root, "project", ".harbor", "skills");

    [Before(Test)]
    public void CreateTempRoot() =>
        _root = Path.Combine(
            Path.GetTempPath(), $"harbor-skill-path-wiring-{Guid.NewGuid():N}");

    [After(Test)]
    public void DeleteTempRoot()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task ExecuteAsync_ParentTraversalName_CannotReadOutsideTheSkillsRoot()
    {
        // The file a '../secret' name would resolve to, one directory above the
        // skills root. A bypass would surface this content in the tool result.
        string outside = Path.Combine(_root, "project", ".harbor", "secret", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "ESCAPED SECRET");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args("../secret"), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).DoesNotContain("ESCAPED SECRET");
    }

    [Test]
    public async Task ExecuteAsync_LeadingDotName_IsRefusedByTheWiredGuard()
    {
        // The old IsSafeName accepted '.hidden' (no '..' substring, all letters
        // and dots), so a file here would have loaded. The validator rejects a
        // segment that starts with a dot, so this refusal proves the validator
        // is the guard in the live path, not a leftover of the old check.
        string hidden = Path.Combine(Skills, ".hidden", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
        File.WriteAllText(hidden, "HIDDEN BODY");

        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(".hidden"), TestToolContext.Create());

        await Assert.That(result).HasFailed();
        await Assert.That(result.Output).DoesNotContain("HIDDEN BODY");
    }

    private SkillTool NewTool() =>
        new(null, NullLogger<SkillTool>.Instance, Skills, null);

    private static JsonElement Args(string name) =>
        JsonDocument.Parse($$"""{"name":"{{name}}"}""").RootElement;
}
