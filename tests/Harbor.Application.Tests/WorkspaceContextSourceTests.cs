using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     ROP-C Z3: the prompt builder rendered skills / context-file sections,
///     but AgentLoop hardcoded empty arrays. WorkspaceContextSource feeds them
///     from the workspace; these tests pin the discovery contract.
/// </summary>
public class WorkspaceContextSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("harbor-wcs").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Test]
    public async Task LoadContextFiles_EmptyDirectory_ReturnsEmpty()
    {
        var files = WorkspaceContextSource.LoadContextFiles(_dir);

        await Assert.That(files).IsEmpty();
    }

    [Test]
    public async Task LoadContextFiles_AgentsAndClaudeFiles_LoadedInOrder()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "AGENTS.md"), "# agents rules");
        await File.WriteAllTextAsync(Path.Combine(_dir, "CLAUDE.md"), "# claude rules");
        // A non-context markdown file must NOT be picked up.
        await File.WriteAllTextAsync(Path.Combine(_dir, "README.md"), "# readme");

        var files = WorkspaceContextSource.LoadContextFiles(_dir);

        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(files[0].Path).IsEqualTo("AGENTS.md");
        await Assert.That(files[0].Content).IsEqualTo("# agents rules");
        await Assert.That(files[1].Path).IsEqualTo("CLAUDE.md");
    }

    [Test]
    public async Task LoadSkills_EmptyDirectories_ReturnEmpty()
    {
        var skills = WorkspaceContextSource.LoadSkills(_dir);

        await Assert.That(skills).IsEmpty();
    }

    [Test]
    public async Task LoadSkills_ProjectSkill_DiscoveredWithProseDescription()
    {
        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        Directory.CreateDirectory(projectSkills);
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "deploy.md"),
            "# Deploy Skill\nRun the deployment pipeline for the current repo.\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Name).IsEqualTo("deploy");
        await Assert.That(skills[0].Description).IsEqualTo("Deploy Skill");
        await Assert.That(skills[0].FilePath).Contains(".harbor/skills");
    }

    [Test]
    public async Task LoadSkills_FrontMatterDescription_Parsed()
    {
        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        Directory.CreateDirectory(projectSkills);
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "review.md"),
            "---\nname: review\ndescription: Reviews pull requests with the house style\n---\n# Review\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir, globalSkillsDir: null);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Description).IsEqualTo("Reviews pull requests with the house style");
    }

    [Test]
    public async Task LoadSkills_ProjectShadowsGlobal_OnNameCollision()
    {
        string globalSkills = Path.Combine(_dir, "global-home", ".harbor", "skills");
        Directory.CreateDirectory(globalSkills);
        await File.WriteAllTextAsync(Path.Combine(globalSkills, "deploy.md"), "# Global deploy\n");

        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        Directory.CreateDirectory(projectSkills);
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "deploy.md"), "# Project deploy\n");

        var skills = WorkspaceContextSource.LoadSkills(
            _dir, Path.Combine(_dir, "global-home", ".harbor", "skills"));

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].FilePath).Contains(".harbor/skills/deploy.md");
        await Assert.That(File.ReadAllText(skills[0].FilePath)).DoesNotContain("Global");
    }

    [Test]
    public async Task LoadSkills_NonMarkdown_Ignored_SubdirectoriesIgnored()
    {
        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        Directory.CreateDirectory(Path.Combine(projectSkills, "nested"));
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "real.md"), "# Real\n");
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "notes.txt"), "ignore me");
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "nested", "deep.md"), "# Deep\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Name).IsEqualTo("real");
    }
}
