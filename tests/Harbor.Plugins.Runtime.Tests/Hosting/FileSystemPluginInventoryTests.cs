using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Tests for <see cref="FileSystemPluginInventory" /> (#1738) — the one
///     answer to "what is installed", and the one way to change it.
/// </summary>
public class FileSystemPluginInventoryTests
{
    private string _root = string.Empty;
    private string GlobalRoot => Path.Combine(_root, "global", "plugins");
    private string ProjectRoot => Path.Combine(_root, "project", ".harbor", "plugins");

    [Before(Test)]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), $"harbor-plugin-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(GlobalRoot);
        Directory.CreateDirectory(ProjectRoot);
    }

    [After(Test)]
    public void DeleteTempRoot()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private FileSystemPluginInventory NewInventory() =>
        new(GlobalRoot, ProjectRoot, NullLogger<FileSystemPluginInventory>.Instance);

    [Test]
    public async Task List_EmptyScopes_ReportsNoPlugins()
    {
        Result<IReadOnlyList<InstalledPluginEntry>> listed = NewInventory().List();

        await Assert.That(listed.IsSuccess).IsTrue();
        await Assert.That(listed.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task List_ReportsBothScopes_AndTheEnabledState()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");
        File.WriteAllText(Path.Combine(GlobalRoot, "Beta.cs.disabled"), "// plugin");
        File.WriteAllText(Path.Combine(ProjectRoot, "Gamma.cs"), "// plugin");

        Result<IReadOnlyList<InstalledPluginEntry>> listed = NewInventory().List();

        await Assert.That(listed.IsSuccess).IsTrue();
        await Assert.That(listed.Value.Count).IsEqualTo(3);

        InstalledPluginEntry? alpha = null;
        InstalledPluginEntry? beta = null;
        InstalledPluginEntry? gamma = null;
        for (int i = 0; i < listed.Value.Count; i++)
        {
            if (listed.Value[i].Name == "Alpha.cs") alpha = listed.Value[i];
            if (listed.Value[i].Name == "Beta.cs") beta = listed.Value[i];
            if (listed.Value[i].Name == "Gamma.cs") gamma = listed.Value[i];
        }

        await Assert.That(alpha).IsNotNull();
        await Assert.That(alpha!.Scope).IsEqualTo(PluginScope.Global);
        await Assert.That(alpha.Enabled).IsTrue();

        await Assert.That(beta).IsNotNull();
        await Assert.That(beta!.Enabled).IsFalse()
            .Because("a '.cs.disabled' file is installed but the loader's glob skips it");

        await Assert.That(gamma).IsNotNull();
        await Assert.That(gamma!.Scope).IsEqualTo(PluginScope.Project);
    }

    [Test]
    public async Task List_ReadsTheManifestAsData()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");
        File.WriteAllText(
            Path.Combine(GlobalRoot, "Alpha.harbor-plugin.json"),
            """{"schemaVersion":1,"name":"alpha","version":"1.2.0","harbor":"0.4.0","axes":["IToolPlugin"]}""");

        Result<IReadOnlyList<InstalledPluginEntry>> listed = NewInventory().List();

        await Assert.That(listed.IsSuccess).IsTrue();
        InstalledPluginEntry entry = listed.Value[0];
        await Assert.That(entry.DeclaredVersion).IsEqualTo("1.2.0");
        await Assert.That(entry.RequiredHarborVersion).IsEqualTo("0.4.0");
        await Assert.That(entry.Compatible).IsTrue();
        await Assert.That(entry.DeclaredAxes.Count).IsEqualTo(1);
        await Assert.That(entry.DeclaredAxes[0]).IsEqualTo("IToolPlugin");
    }

    [Test]
    public async Task List_ReportsAHarborFloorThisBuildDoesNotMeet()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");
        File.WriteAllText(
            Path.Combine(GlobalRoot, "Alpha.harbor-plugin.json"),
            """{"schemaVersion":1,"harbor":"99.0.0"}""");

        Result<IReadOnlyList<InstalledPluginEntry>> listed = NewInventory().List();

        await Assert.That(listed.IsSuccess).IsTrue();
        await Assert.That(listed.Value[0].Compatible).IsFalse()
            .Because("a plugin that needs a newer Harbor will be refused at load time, and the agent "
                     + "has to learn that from the inventory");
    }

    [Test]
    public async Task List_ReportsABrokenManifest_InsteadOfPretendingThereIsNone()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.harbor-plugin.json"), "{ not json");

        Result<IReadOnlyList<InstalledPluginEntry>> listed = NewInventory().List();

        await Assert.That(listed.IsSuccess).IsTrue();
        await Assert.That(listed.Value[0].ManifestError).IsNotNull()
            .Because("a declaration the host cannot read is a refusal of the plugin, not an absent one");
    }

    [Test]
    public async Task Install_CopiesTheSourceAndItsManifest()
    {
        string source = Path.Combine(_root, "Source.cs");
        File.WriteAllText(source, "// plugin");
        File.WriteAllText(Path.Combine(_root, "Source.harbor-plugin.json"), """{"schemaVersion":1}""");

        Result<PluginInventoryChangeResult> applied = await NewInventory().ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Install, "Alpha.cs", source, PluginScope.Global));

        await Assert.That(applied.IsSuccess).IsTrue();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.cs"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.harbor-plugin.json"))).IsTrue()
            .Because("the declaration travels with the plugin");
    }

    [Test]
    public async Task Install_RefusesAnExistingPlugin()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");
        string source = Path.Combine(_root, "Source.cs");
        File.WriteAllText(source, "// plugin");

        Result<PluginInventoryChangeResult> applied = await NewInventory().ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Install, "Alpha.cs", source, PluginScope.Global));

        await Assert.That(applied.IsFailure).IsTrue();
        await Assert.That(applied.Error).Contains("already installed");
    }

    [Test]
    public async Task Install_RefusesASourceThatIsNotAPluginSource()
    {
        string source = Path.Combine(_root, "notes.txt");
        File.WriteAllText(source, "not a plugin");

        Result<PluginInventoryChangeResult> applied = await NewInventory().ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Install, "Alpha.cs", source, PluginScope.Global));

        await Assert.That(applied.IsFailure).IsTrue();
        await Assert.That(applied.Error).Contains(".cs");
    }

    [Test]
    public async Task Uninstall_RemovesThePluginAndItsManifest()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.harbor-plugin.json"), """{"schemaVersion":1}""");

        Result<PluginInventoryChangeResult> applied = await NewInventory().ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Uninstall, "Alpha.cs"));

        await Assert.That(applied.IsSuccess).IsTrue();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.cs"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.harbor-plugin.json"))).IsFalse();
    }

    [Test]
    public async Task Uninstall_RefusesANameThatCarriesADirectoryPart()
    {
        Result<PluginInventoryChangeResult> applied = await NewInventory().ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Uninstall, "../../etc/passwd"));

        await Assert.That(applied.IsFailure).IsTrue();
        await Assert.That(applied.Error).Contains("not a plugin file name");
    }

    [Test]
    public async Task Disable_AndEnable_ToggleTheSuffixTheLoaderSkips()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");

        FileSystemPluginInventory inventory = NewInventory();

        Result<PluginInventoryChangeResult> disabled = await inventory.ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Disable, "Alpha.cs"));
        await Assert.That(disabled.IsSuccess).IsTrue();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.cs.disabled"))).IsTrue();

        Result<IReadOnlyList<InstalledPluginEntry>> afterDisable = inventory.List();
        await Assert.That(afterDisable.Value[0].Enabled).IsFalse();

        Result<PluginInventoryChangeResult> enabled = await inventory.ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Enable, "Alpha.cs"));
        await Assert.That(enabled.IsSuccess).IsTrue();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.cs"))).IsTrue();
    }

    [Test]
    public async Task Enable_OfAnEnabledPlugin_Refuses()
    {
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// plugin");

        Result<PluginInventoryChangeResult> applied = await NewInventory().ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Enable, "Alpha.cs"));

        await Assert.That(applied.IsFailure).IsTrue();
        await Assert.That(applied.Error).Contains("already enabled");
    }

    [Test]
    public async Task Uninstall_FindsTheProjectScopeFirst()
    {
        // Two plugins with the same name, one per scope. The project scope
        // wins — the same order the CLI's `harbor plugin uninstall` uses — so
        // the file that disappears is the project's, and the user's stays.
        File.WriteAllText(Path.Combine(GlobalRoot, "Alpha.cs"), "// global");
        File.WriteAllText(Path.Combine(ProjectRoot, "Alpha.cs"), "// project");

        FileSystemPluginInventory inventory = NewInventory();
        Result<PluginInventoryChangeResult> applied = await inventory.ApplyAsync(
            new PluginInventoryChange(PluginInventoryAction.Uninstall, "Alpha.cs"));

        await Assert.That(applied.IsSuccess).IsTrue();
        await Assert.That(applied.Value.FullPath).StartsWith(ProjectRoot)
            .Because("the project scope is resolved first");
        await Assert.That(File.Exists(Path.Combine(ProjectRoot, "Alpha.cs"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(GlobalRoot, "Alpha.cs"))).IsTrue();
    }
}
