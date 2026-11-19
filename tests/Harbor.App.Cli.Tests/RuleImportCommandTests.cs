// #3409: proof that RuleImporter has a real product consumer. These tests
// drive `harbor import-rules` through the same ConsoleAppFramework route a
// user calls and assert the observable result: the legacy rule text lands in
// the project's AGENTS.md inside the marker block RuleImporter produces.

using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

[NotInParallel]
public sealed class RuleImportCommandTests : CafVerbTestBase, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "harbor-import-rules-tests-" + Guid.NewGuid().ToString("N"));

    public RuleImportCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task ImportRules_CreatesRulesInAgentsFile()
    {
        string rulesPath = Path.Combine(_root, ".cursorrules");
        await File.WriteAllTextAsync(rulesPath, "Always use TUnit for tests.\r\nBe terse.");

        var (exit, output, error) =
            await RunCafAsync("import-rules", ["--project", _root, rulesPath]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).Contains("Imported 1 rule file(s)");

        string agentsPath = Path.Combine(_root, "AGENTS.md");
        await Assert.That(File.Exists(agentsPath)).IsTrue();
        string agents = await File.ReadAllTextAsync(agentsPath);
        await Assert.That(agents).Contains("harbor:imported-rules:start");
        await Assert.That(agents).Contains("harbor:imported-rules:end");
        await Assert.That(agents).Contains("Project rules (migrated from `.cursorrules`)");
        await Assert.That(agents).Contains("Always use TUnit for tests.");
        await Assert.That(agents).DoesNotContain("\r\n"); // CRLF normalised by the leaf
    }

    [Test]
    public async Task ImportRules_Reimport_ReplacesPreviousAppendix()
    {
        string rulesPath = Path.Combine(_root, ".kilocoderules");
        await File.WriteAllTextAsync(rulesPath, "First version.");

        var first = await RunCafAsync("import-rules", ["--project", _root, rulesPath]);
        await Assert.That(first.Exit).IsEqualTo(0);

        await File.WriteAllTextAsync(rulesPath, "Second version.");
        var second = await RunCafAsync("import-rules", ["--project", _root, rulesPath]);

        await Assert.That(second.Exit).IsEqualTo(0);
        string agents = await File.ReadAllTextAsync(Path.Combine(_root, "AGENTS.md"));
        await Assert.That(agents).Contains("Second version.");
        await Assert.That(agents).DoesNotContain("First version.");
    }

    [Test]
    public async Task ImportRules_UnknownRuleFile_FailsWithoutWritingAgents()
    {
        string rulesPath = Path.Combine(_root, "README.md");
        await File.WriteAllTextAsync(rulesPath, "not a legacy rule file");

        var (exit, _, error) =
            await RunCafAsync("import-rules", ["--project", _root, rulesPath]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains("not a recognised legacy rule file");
        await Assert.That(File.Exists(Path.Combine(_root, "AGENTS.md"))).IsFalse();
    }

    [Test]
    public async Task ImportRules_Help_ExitsZeroWithUsage() =>
        await AssertHelpExitsZeroWithUsage("import-rules");
}
