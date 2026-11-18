using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

public class PromptsCommandTests : IDisposable
{
    private readonly string _base;
    private readonly string _homeRoot;
    private readonly string _projectRoot;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public PromptsCommandTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "harbor-prompt-tests-" + Guid.NewGuid().ToString("N"));
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

    private PromptsCommand CreateCommand(Func<string, Task<int>>? sender = null) =>
        new(_out, _err, globalRoot: _homeRoot, projectRoot: _projectRoot, sender: sender);

    private async Task WritePrompt(string root, string name, string text) =>
        await File.WriteAllTextAsync(Path.Combine(root, name + ".md"), text);

    [Test]
    public async Task List_EmptyScopes_ReportsNone()
    {
        int exit = await CreateCommand().ExecuteAsync(["list"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString()).Contains("(no prompts installed)");
    }

    [Test]
    public async Task List_ShowsPrompts_ByScope_WithDescriptions()
    {
        await WritePrompt(_homeRoot, "review", "---\ndescription: Global review\n---\nReview the diff.");
        await WritePrompt(_projectRoot, "ship", "---\ndescription: Project ship\n---\nShip it.");

        int exit = await CreateCommand().ExecuteAsync(["list"]);

        await Assert.That(exit).IsEqualTo(0);
        string output = _out.ToString();
        await Assert.That(output).Contains("review");
        await Assert.That(output).Contains("[global]");
        await Assert.That(output).Contains("Global review");
        await Assert.That(output).Contains("ship");
        await Assert.That(output).Contains("[project]");
        await Assert.That(output).Contains("Project ship");
    }

    [Test]
    public async Task List_ProjectShadowsGlobal_ByName()
    {
        await WritePrompt(_homeRoot, "ship", "global body");
        await WritePrompt(_projectRoot, "ship", "project body");

        await CreateCommand().ExecuteAsync(["list"]);

        string output = _out.ToString();
        await Assert.That(output.Split("ship", StringSplitOptions.None).Length - 1).IsEqualTo(1);
        await Assert.That(output).Contains("[project]");
        await Assert.That(output).DoesNotContain("[global]");
    }

    [Test]
    public async Task Show_PrintsBodyWithoutFrontmatter()
    {
        await WritePrompt(_homeRoot, "review", "---\ndescription: ignored here\n---\nLine one\nLine two");

        int exit = await CreateCommand().ExecuteAsync(["show", "review"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString().Trim()).IsEqualTo("Line one\nLine two");
    }

    [Test]
    public async Task Show_PlainFile_PrintsWholeBody()
    {
        await WritePrompt(_homeRoot, "plain", "  Just a prompt.  ");

        int exit = await CreateCommand().ExecuteAsync(["show", "plain"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString().Trim()).IsEqualTo("Just a prompt.");
    }

    [Test]
    public async Task Show_UnknownName_ReturnsOne()
    {
        int exit = await CreateCommand().ExecuteAsync(["show", "nope"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("No prompt named 'nope'");
    }

    [Test]
    public async Task Send_SubstitutesArgsAndPositionals_AndCallsSender()
    {
        await WritePrompt(_projectRoot, "review", "---\ndescription: review\n---\nReview {{1}} against {{2}}: {{args}}");
        string? sent = null;
        int calls = 0;
        var cmd = CreateCommand(text =>
        {
            sent = text;
            calls++;
            return Task.FromResult(42);
        });

        int exit = await cmd.ExecuteAsync(["send", "review", "src", "main", "extra"]);

        await Assert.That(exit).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(sent).IsEqualTo("Review src against main: src main extra");
    }

    [Test]
    public async Task Send_NoPlaceholders_ForwardsBodyVerbatim()
    {
        await WritePrompt(_homeRoot, "ship", "Run the full release checklist.");
        string? sent = null;
        var cmd = CreateCommand(text =>
        {
            sent = text;
            return Task.FromResult(0);
        });

        int exit = await cmd.ExecuteAsync(["send", "ship", "ignored"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sent).IsEqualTo("Run the full release checklist.");
    }

    [Test]
    public async Task Send_ProjectShadowsGlobal()
    {
        await WritePrompt(_homeRoot, "ship", "global body");
        await WritePrompt(_projectRoot, "ship", "project body");
        string? sent = null;
        var cmd = CreateCommand(text =>
        {
            sent = text;
            return Task.FromResult(0);
        });

        await cmd.ExecuteAsync(["send", "ship"]);

        await Assert.That(sent).IsEqualTo("project body");
    }

    [Test]
    public async Task Send_UnknownName_DoesNotCallSender()
    {
        int calls = 0;
        var cmd = CreateCommand(_ =>
        {
            calls++;
            return Task.FromResult(0);
        });

        int exit = await cmd.ExecuteAsync(["send", "missing"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(_err.ToString()).Contains("No prompt named 'missing'");
    }

    [Test]
    public async Task Send_MissingName_ReturnsUsage()
    {
        int exit = await CreateCommand().ExecuteAsync(["send"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(_err.ToString()).Contains("Usage: harbor prompt send");
    }

    [Test]
    public async Task Install_ChoosesScopeFromTheFlag()
    {
        var source = Path.Combine(_base, "scope.md");
        await File.WriteAllTextAsync(source, "scope body");

        int toGlobal = await CreateCommand().ExecuteAsync(["install", source]);
        await Assert.That(toGlobal).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_homeRoot, "scope.md"))).IsTrue();

        _out.GetStringBuilder().Clear();
        int toProject = await CreateCommand().ExecuteAsync(["install", source, "--project"]);
        await Assert.That(toProject).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(_projectRoot, "scope.md"))).IsTrue();
    }

    [Test]
    public async Task Install_WithoutForce_RejectsOverwrite()
    {
        var source = Path.Combine(_base, "dup.md");
        await File.WriteAllTextAsync(source, "first");
        int first = await CreateCommand().ExecuteAsync(["install", source]);
        await Assert.That(first).IsEqualTo(0);

        _err.GetStringBuilder().Clear();
        int second = await CreateCommand().ExecuteAsync(["install", source]);
        await Assert.That(second).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("--force");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(_homeRoot, "dup.md"))).IsEqualTo("first");
    }

    [Test]
    public async Task Install_Existing_WithForce_Overwrites()
    {
        string source = Path.Combine(_base, "dup.md");
        await File.WriteAllTextAsync(source, "first");
        await CreateCommand().ExecuteAsync(["install", source]);
        await File.WriteAllTextAsync(source, "second");

        int exit = await CreateCommand().ExecuteAsync(["install", source, "--force"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(_homeRoot, "dup.md"))).IsEqualTo("second");
    }

    [Test]
    public async Task Install_InvalidName_FailsClosed()
    {
        string source = Path.Combine(_base, "bad name.md");
        await File.WriteAllTextAsync(source, "body");

        int exit = await CreateCommand().ExecuteAsync(["install", source]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("Invalid prompt name");
    }

    [Test]
    public async Task Uninstall_RemovesPromptFromEitherScope()
    {
        await WritePrompt(_homeRoot, "global-only", "body");
        await WritePrompt(_projectRoot, "project-only", "body");

        await Assert.That(await CreateCommand().ExecuteAsync(["uninstall", "global-only"])).IsEqualTo(0);
        await Assert.That(await CreateCommand().ExecuteAsync(["uninstall", "project-only"])).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(_homeRoot, "global-only.md"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_projectRoot, "project-only.md"))).IsFalse();
    }

    [Test]
    public async Task Uninstall_Missing_ReturnsOne()
    {
        int exit = await CreateCommand().ExecuteAsync(["uninstall", "nope"]);

        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownSubcommand_ReturnsUsage()
    {
        int exit = await CreateCommand().ExecuteAsync(["bogus"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(_err.ToString()).Contains("Usage: harbor prompt");
    }
}
