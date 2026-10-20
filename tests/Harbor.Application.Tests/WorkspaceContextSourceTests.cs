using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
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

    /// <summary>
    ///     #1580: the front-matter field helper matches the prefix
    ///     case-insensitively regardless of the field's letter case.
    /// </summary>
    [Test]
    public async Task LoadSkills_FrontMatterFields_CaseInsensitivePrefix()
    {
        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        Directory.CreateDirectory(projectSkills);
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "review.md"),
            "---\nNAME: review\nDESCRIPTION: Reviews pull requests\n---\n# Review\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir, globalSkillsDir: null);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Name).IsEqualTo("review");
        await Assert.That(skills[0].Description).IsEqualTo("Reviews pull requests");
    }

    /// <summary>
    ///     #1580: leading indentation before the field prefix is tolerated —
    ///     the helper trims the line before matching.
    /// </summary>
    [Test]
    // #1770: the skill-directory vectors used to be one copy-pasted method
    // each. The assert never changed, only the (content, name, description)
    // triple did. Every row keeps its original test name as the case display
    // name.
    [Arguments("---\n    name: deploy\n    description: Ships the branch to staging\n---\n# Deploy\n", "deploy", "Ships the branch to staging", DisplayName = "LoadSkills_FrontMatterFields_IndentedValue_Parsed")]
    // #1580: the helper maps an empty field value to null, so the
    // directory-name fallback applies for <c>name:</c> and the
    // prose/markdown fallback applies for <c>description:</c>.
    [Arguments("---\nname:\ndescription:\n---\n# Deploy\nRun it.\n", "deploy", "Deploy", DisplayName = "LoadSkills_FrontMatterEmptyValues_FallBack")]
    [Arguments("# Deploy\nRun it.\n", "deploy", "Deploy", DisplayName = "LoadSkills_SkillDirectory_FallsBackToDirectoryName")]
    public async Task LoadSkills_SkillDirectory_ReadsNameAndDescription(string content, string expectedName, string expectedDescription)
    {
        string skillDir = Path.Combine(_dir, ".harbor", "skills", "deploy");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), content);

        var skills = WorkspaceContextSource.LoadSkills(_dir);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Name).IsEqualTo(expectedName);
        await Assert.That(skills[0].Description).IsEqualTo(expectedDescription);
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
        // Path.Combine emits '\' on Windows — compare separator-agnostically.
        await Assert.That(skills[0].FilePath.Replace('\\', '/')).Contains(".harbor/skills/deploy.md");
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

    [Test]
    public async Task LoadSkills_SkillDirectory_DiscoveredWithFrontMatterName()
    {
        string skillDir = Path.Combine(_dir, ".harbor", "skills", "review");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: review\ndescription: Reviews pull requests\n---\n# Review\nDo the review.\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Name).IsEqualTo("review");
        await Assert.That(skills[0].Description).IsEqualTo("Reviews pull requests");
        await Assert.That(skills[0].FilePath.Replace('\\', '/')).Contains(".harbor/skills/review/SKILL.md");
    }

    [Test]
    public async Task LoadSkills_FlatFileWinsOverSkillDirectory_SameRoot()
    {
        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        string skillDir = Path.Combine(projectSkills, "deploy");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "# Dir deploy\n");
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "deploy.md"), "# Flat deploy\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].FilePath.Replace('\\', '/')).Contains("deploy.md");
    }

    /// <summary>
    ///     #1240: skill discovery returns a pre-sorted (ordinal by name) frozen
    ///     snapshot — the per-turn consumer iterates it as-is. Files land in
    ///     enumeration order and front-matter names can reorder within a root,
    ///     so the sort must be total, not per-root.
    /// </summary>
    [Test]
    public async Task LoadSkills_ReturnsPreSortedByNameOrdinal()
    {
        string projectSkills = Path.Combine(_dir, ".harbor", "skills");
        Directory.CreateDirectory(projectSkills);
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "zeta.md"), "# Zeta\n");
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "alpha.md"), "# Alpha\n");
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "delta.md"), "# Delta\n");

        var skills = WorkspaceContextSource.LoadSkills(_dir, globalSkillsDir: null);

        await Assert.That(skills.Count).IsEqualTo(3);
        await Assert.That(skills[0].Name).IsEqualTo("alpha");
        await Assert.That(skills[1].Name).IsEqualTo("delta");
        await Assert.That(skills[2].Name).IsEqualTo("zeta");
    }

    // ---- ROP-D Z3: MCP instruction aggregation formatting ------------------

    [Test]
    public async Task FormatMcpInstructions_NullOrEmpty_ReturnsNull()
    {
        await Assert.That(WorkspaceContextSource.FormatMcpInstructions(null)).IsNull();
        await Assert.That(
            WorkspaceContextSource.FormatMcpInstructions(Array.Empty<McpServerInstructions>())).IsNull();
    }

    [Test]
    public async Task FormatMcpInstructions_MultipleServers_RendersOneBulletPerServer()
    {
        var servers = new[]
        {
            new McpServerInstructions("filesystem", "Read files only."),
            new McpServerInstructions("git", "Never force-push.\nUse dry runs."),
        };

        string? block = WorkspaceContextSource.FormatMcpInstructions(servers);

        await Assert.That(block).IsNotNull();
        await Assert.That(block!).Contains("- filesystem: Read files only.");
        // Multi-line server text is flattened to one line per bullet.
        await Assert.That(block).Contains("- git: Never force-push. Use dry runs.");
    }

    // ---- #1523: the snapshot cache is instance state, not a static global --

    /// <summary>
    ///     #1523: one instance's cache hit reuses the same snapshot lists —
    ///     reference identity proves the fast path, not a reload.
    /// </summary>
    [Test]
    public async Task GetOrLoadCached_SecondCall_SameInstance_ReusesCachedSnapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "AGENTS.md"), "# agents rules");
        var source = new WorkspaceContextService();

        var (first, _) = source.GetOrLoadCached(_dir);
        var (second, _) = source.GetOrLoadCached(_dir);

        await Assert.That(second).IsSameReferenceAs(first);
    }

    /// <summary>
    ///     #1523: fresh instances own fresh caches — the old static global
    ///     would let a second "instance" see the first one's snapshot.
    /// </summary>
    [Test]
    public async Task GetOrLoadCached_DistinctInstances_DoNotShareCache()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "AGENTS.md"), "# agents rules");
        var first = new WorkspaceContextService();
        var second = new WorkspaceContextService();

        var (firstFiles, _) = first.GetOrLoadCached(_dir);
        var (secondFiles, _) = second.GetOrLoadCached(_dir);

        await Assert.That(secondFiles).IsNotSameReferenceAs(firstFiles);
    }

    /// <summary>
    ///     #1523: <c>Invalidate</c> evicts on the instance it was called on
    ///     only — the replacing fixture/scope isolation story for the old
    ///     static test hook.
    /// </summary>
    [Test]
    public async Task Invalidate_InstanceScoped_EvictsOnlyThatInstance()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "AGENTS.md"), "# agents rules");
        var evicted = new WorkspaceContextService();
        var untouched = new WorkspaceContextService();

        var (evictedFirst, _) = evicted.GetOrLoadCached(_dir);
        var (untouchedFirst, _) = untouched.GetOrLoadCached(_dir);
        evicted.Invalidate(_dir);
        var (evictedSecond, _) = evicted.GetOrLoadCached(_dir);
        var (untouchedSecond, _) = untouched.GetOrLoadCached(_dir);

        await Assert.That(evictedSecond).IsNotSameReferenceAs(evictedFirst);
        await Assert.That(untouchedSecond).IsSameReferenceAs(untouchedFirst);
    }
}
