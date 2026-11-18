using System.Text.Json;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Behavior tests for <c>harbor tools list|show --json</c> (#2675 item 030).
///     The command is fed a registry snapshot, so no host or provider key is
///     needed and the output contract is pinned directly.
/// </summary>
public class ToolsCommandTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private ToolsCommand CreateCommand() => new(
        _out,
        _err,
        () => new[]
        {
            Descriptor("write", "Write a file"),
            Descriptor("read", "Read a file")
        });

    private static ToolDescriptor Descriptor(string name, string description) => new(
        ToolName.Create(name),
        displayName: name,
        description: description,
        schema: JsonDocument.Parse("""{"type":"object","properties":{}}"""),
        executionMode: ExecutionMode.Parallel,
        promptSnippet: null,
        promptGuidelines: []);

    [Test]
    public async Task List_Text_IsSortedByName()
    {
        int exit = await CreateCommand().ExecuteAsync(["list"]);

        await Assert.That(exit).IsEqualTo(0);
        string output = _out.ToString();
        await Assert.That(output.IndexOf("read", StringComparison.Ordinal))
            .IsLessThan(output.IndexOf("write", StringComparison.Ordinal));
        await Assert.That(output).Contains("Read a file");
    }

    [Test]
    public async Task List_Json_EmitsToolArray()
    {
        int exit = await CreateCommand().ExecuteAsync(["list", "--json"]);

        await Assert.That(exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(_out.ToString());
        var tools = doc.RootElement.GetProperty("tools");
        await Assert.That(tools.GetArrayLength()).IsEqualTo(2);
        await Assert.That(tools[0].GetProperty("name").GetString()).IsEqualTo("read");
        await Assert.That(tools[0].TryGetProperty("inputSchema", out _)).IsFalse();
    }

    [Test]
    public async Task Show_Json_IncludesFullDescriptor()
    {
        int exit = await CreateCommand().ExecuteAsync(["show", "read", "--json"]);

        await Assert.That(exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(_out.ToString());
        var tool = doc.RootElement.GetProperty("tools")[0];
        await Assert.That(tool.GetProperty("name").GetString()).IsEqualTo("read");
        await Assert.That(tool.GetProperty("executionMode").GetString()).IsEqualTo("Parallel");
        await Assert.That(tool.GetProperty("inputSchema").GetProperty("type").GetString()).IsEqualTo("object");
    }

    [Test]
    public async Task Show_UnknownTool_FailsWithoutOutput()
    {
        int exit = await CreateCommand().ExecuteAsync(["show", "missing"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("Unknown tool: missing");
        await Assert.That(_out.ToString()).IsEmpty();
    }

    [Test]
    public async Task BadSubcommand_FailsWithUsage()
    {
        int exit = await CreateCommand().ExecuteAsync(["bogus"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(_err.ToString()).Contains("Usage: harbor tools");
    }
}
