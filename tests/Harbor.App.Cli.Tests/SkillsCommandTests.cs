using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

public class SkillsCommandTests : IDisposable
{
    private readonly string _base;
    private readonly string _homeRoot;
    private readonly string _projectRoot;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public SkillsCommandTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "harbor-skill-tests-" + Guid.NewGuid().ToString("N"));
        _homeRoot = Path.Combine(_base, "global");
        _projectRoot = Path.Combine(_base, "project");
        Directory.CreateDirectory(_homeRoot);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SkillsCommand CreateCommand() => new(_out, _err, globalRoot: _homeRoot, projectRoot: _projectRoot);

    [Test]
    public async Task List_EmptyScopes_ReportsNone()
    {
        int exit = await CreateCommand().ExecuteAsync(["list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString()).Contains("(no skills installed)");
    }

    [Test]
    public async Task List_ShowsSkills_ByScope_WithDescriptions()
    {
        Directory.CreateDirectory(Path.Combine(_homeRoot, "review"));
        await File.WriteAllTextAsync(Path.Combine(_homeRoot, "review", "SKILL.md"),
            "---\ndescription: Global review\n---\n# Review\n");
        await File.WriteAllTextAsync(Path.Combine(_projectRoot, "deploy.md"), "# Deploy it\n");

        int exit = await CreateCommand().ExecuteAsync(["list"]);

        await Assert.That(exit).IsEqualTo(0);
        string output = _out.ToString();
        await Assert.That(output).Contains("review");
        await Assert.That(output).Contains("[global]");
        await Assert.That(output).Contains("deploy");
        await Assert.That(output).Contains("[project]");
    }

    [Test]
    public async Task Install_Directory_CopiesSkillMd_IntoGlobalRoot()
    {
        string sourceDir = Path.Combine(_base, "src-review");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Review\n");

        int exit = await CreateCommand().ExecuteAsync(["install", sourceDir]);

        await Assert.That(exit).IsEqualTo(0);
        string installed = Path.Combine(_homeRoot, "src-review", "SKILL.md");
        await Assert.That(File.Exists(installed)).IsTrue();
    }

    [Test]
    public async Task Install_FlatFile_ProjectFlag_TargetsProjectScope()
    {
        string source = Path.Combine(_base, "notes.md");
        await File.WriteAllTextAsync(source, "# Notes\n");

        int exit = await CreateCommand().ExecuteAsync(["install", source, "--project"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_projectRoot, "notes.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(_homeRoot, "notes.md"))).IsFalse();
    }

    [Test]
    public async Task Install_Existing_WithoutForce_Fails()
    {
        string source = Path.Combine(_base, "dup.md");
        await File.WriteAllTextAsync(source, "# Dup\n");
        await Assert.That(await CreateCommand().ExecuteAsync(["install", source])).IsEqualTo(0);

        _err.GetStringBuilder().Clear();
        int exit = await CreateCommand().ExecuteAsync(["install", source]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("--force");
    }

    [Test]
    public async Task Uninstall_RemovesSkillDirectory()
    {
        string dir = Path.Combine(_projectRoot, "gone");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), "# Gone\n");

        int exit = await CreateCommand().ExecuteAsync(["uninstall", "gone"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Directory.Exists(dir)).IsFalse();
    }

    [Test]
    public async Task Uninstall_Missing_ReturnsOne()
    {
        int exit = await CreateCommand().ExecuteAsync(["uninstall", "nope"]);

        await Assert.That(exit).IsEqualTo(1);
    }
}
