using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

public class PluginsCommandTests : IDisposable
{
    private readonly string _homeRoot;
    private readonly string _projectRoot;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public PluginsCommandTests()
    {
        _homeRoot = Path.Combine(Path.GetTempPath(), "harbor-plugin-tests-" + Guid.NewGuid().ToString("N"), "global");
        _projectRoot = Path.Combine(Path.GetTempPath(), "harbor-plugin-tests-" + Guid.NewGuid().ToString("N"), "project");
        Directory.CreateDirectory(_homeRoot);
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        Directory.Delete(Path.GetDirectoryName(_homeRoot)!, recursive: true);
        Directory.Delete(Path.GetDirectoryName(_projectRoot)!, recursive: true);
    }

    private PluginsCommand CreateCommand() => new(_out, _err, globalRoot: _homeRoot, projectRoot: _projectRoot);

    private async Task<string> WriteSourceFileAsync(string name, string content = "public sealed class P : IPlugin { }")
    {
        string path = Path.Combine(Path.GetTempPath(), name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Test]
    public async Task List_EmptyScopes_ReportsNone()
    {
        int exit = await CreateCommand().ExecuteAsync(["list"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString()).Contains("(none)");
    }

    [Test]
    public async Task List_ShowsInstalledFiles_ByScope()
    {
        await File.WriteAllTextAsync(Path.Combine(_homeRoot, "Alpha.cs"), "x");
        await File.WriteAllTextAsync(Path.Combine(_projectRoot, "Beta.cs"), "y");

        int exit = await CreateCommand().ExecuteAsync(["list"]);

        await Assert.That(exit).IsEqualTo(0);
        string output = _out.ToString();
        await Assert.That(output).Contains("Alpha.cs");
        await Assert.That(output).Contains("Beta.cs");
    }

    [Test]
    public async Task Install_Global_Copies_Source_Into_Home_Root()
    {
        string source = await WriteSourceFileAsync("hello-world.cs");

        int exit = await CreateCommand().ExecuteAsync(["install", source]);

        await Assert.That(exit).IsEqualTo(0);
        string installed = Path.Combine(_homeRoot, "hello-world.cs");
        await Assert.That(File.Exists(installed)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(installed)).Contains("IPlugin");
    }

    [Test]
    public async Task Install_Project_Flag_Targets_Project_Scope()
    {
        string source = await WriteSourceFileAsync("projplugin.cs");

        int exit = await CreateCommand().ExecuteAsync(["install", source, "--project"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_projectRoot, "projplugin.cs"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(_homeRoot, "projplugin.cs"))).IsFalse();
    }

    [Test]
    public async Task Install_Duplicate_Fails_Without_Force_And_Succeeds_With_Force()
    {
        string source = await WriteSourceFileAsync("dup.cs", "v1");
        var cmd = CreateCommand();

        int first = await cmd.ExecuteAsync(["install", source]);
        File.WriteAllText(source, "v2");
        int second = await cmd.ExecuteAsync(["install", source]);
        int third = await cmd.ExecuteAsync(["install", source, "--force"]);

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
        await Assert.That(third).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(_homeRoot, "dup.cs"))).IsEqualTo("v2");
    }

    [Test]
    public async Task Install_NonCs_File_Is_Rejected()
    {
        string source = Path.Combine(Path.GetTempPath(), "evil.dll.txt");
        await File.WriteAllTextAsync(source, "junk");

        int exit = await CreateCommand().ExecuteAsync(["install", source]);

        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Install_Missing_File_Fails()
    {
        int exit = await CreateCommand().ExecuteAsync(["install", "definitely-missing-9f2a.cs"]);

        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Uninstall_Removes_By_Plain_Name_From_Either_Scope()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectRoot, "bye.cs"), "x");

        int exit = await CreateCommand().ExecuteAsync(["uninstall", "bye"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_projectRoot, "bye.cs"))).IsFalse();
    }

    [Test]
    public async Task Uninstall_Accepts_Name_With_Extension()
    {
        await File.WriteAllTextAsync(Path.Combine(_homeRoot, "gone.cs"), "x");

        int exit = await CreateCommand().ExecuteAsync(["uninstall", "gone.cs"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_homeRoot, "gone.cs"))).IsFalse();
    }

    [Test]
    public async Task Uninstall_Unknown_Name_Fails()
    {
        int exit = await CreateCommand().ExecuteAsync(["uninstall", "no-such-plugin"]);
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Uninstall_Rejects_Path_Traversal_Argument()
    {
        // Crafted argument must not delete a file outside plugin roots.
        string outsideDir = Path.Combine(Path.GetDirectoryName(_homeRoot)!, "outside");
        Directory.CreateDirectory(outsideDir);
        string outsideFile = Path.Combine(outsideDir, "victim.cs");
        await File.WriteAllTextAsync(outsideFile, "keep me");

        int exit = await CreateCommand().ExecuteAsync(["uninstall", $"../outside/victim.cs"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(File.Exists(outsideFile)).IsTrue();
    }
}
