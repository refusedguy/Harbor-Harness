using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Tools;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     Byte-identical error contract for <c>ValidateArguments</c> across all builtin
///     tools (#181): after the JsonArgValidator refactor every message below must read
///     exactly as before — only the validation mechanics changed.
/// </summary>
public class ArgValidationMessageTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static async Task<string?> Error(ITool tool, string json)
    {
        var result = tool.ValidateArguments(Parse(json));
        await Assert.That(result.IsFailure).IsTrue();
        return result.Error;
    }

    private static async Task Ok(ITool tool, string json)
    {
        var result = tool.ValidateArguments(Parse(json));
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Patch_Messages()
    {
        var tool = new PatchTool(NullLogger<PatchTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'path'.");
        await Assert.That(await Error(tool, """{"path":"f"}""")).IsEqualTo("Missing or empty 'patch'.");
        await Ok(tool, """{"path":"f","patch":"@@ -1 +1 @@\n a\n+b"}""");
    }

    [Test]
    public async Task Read_Messages()
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'path'.");
        await Assert.That(await Error(tool, """{"path":"f","offset":0}""")).IsEqualTo("'offset' must be >= 1.");
        await Assert.That(await Error(tool, """{"path":"f","limit":0}""")).IsEqualTo("'limit' must be >= 1.");
        await Ok(tool, """{"path":"f","offset":"x"}""");
    }

    [Test]
    public async Task Bash_Messages()
    {
        var tool = new BashTool(NullLogger<BashTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing required argument 'command'.");
        await Assert.That(await Error(tool, """{"command":123}""")).IsEqualTo("Missing required argument 'command'.");
        await Assert.That(await Error(tool, """{"command":"   "}""")).IsEqualTo("'command' cannot be empty.");
        await Ok(tool, """{"command":"echo hi"}""");
    }

    [Test]
    public async Task Edit_Messages()
    {
        var tool = new EditTool(NullLogger<EditTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'path'.");
        await Assert.That(await Error(tool, """{"path":"f"}""")).IsEqualTo("Provide edits[] or both oldString and newString.");
        await Assert.That(await Error(tool, """{"path":"f","oldString":"","newString":"x"}""")).IsEqualTo("oldString must not be empty.");
        await Assert.That(await Error(tool, """{"path":"f","edits":[{}]}""")).IsEqualTo("Each edit needs non-empty oldString.");
        await Assert.That(await Error(tool, """{"path":"f","edits":[{"oldString":"a"}]}""")).IsEqualTo("Each edit needs newString.");
        await Ok(tool, """{"path":"f","oldString":"a","newString":"b"}""");
    }

    [Test]
    public async Task Write_Messages()
    {
        var tool = new WriteTool(NullLogger<WriteTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'path'.");
        await Assert.That(await Error(tool, """{"path":"f"}""")).IsEqualTo("Missing required argument 'content'.");
        await Ok(tool, """{"path":"f","content":""}""");
    }

    [Test]
    public async Task Ls_Messages()
    {
        var tool = new LsTool(NullLogger<LsTool>.Instance);
        await Assert.That(await Error(tool, """{"depth":0}""")).IsEqualTo("depth must be >= 1");
        await Assert.That(await Error(tool, """{"maxEntries":0}""")).IsEqualTo("maxEntries must be >= 1");
        await Ok(tool, """{"depth":"x"}""");
        await Ok(tool, "{}");
    }

    [Test]
    public async Task Grep_Messages()
    {
        var tool = new GrepTool(NullLogger<GrepTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing required argument 'pattern'.");
        await Ok(tool, """{"pattern":"   "}""");
        var bad = tool.ValidateArguments(Parse("""{"pattern":"(["}"""));
        await Assert.That(bad.IsFailure).IsTrue();
        await Assert.That(bad.Error).Contains("Invalid regex");
    }

    [Test]
    public async Task Glob_Messages()
    {
        var tool = new GlobTool(NullLogger<GlobTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'pattern'.");
        await Assert.That(await Error(tool, """{"pattern":"  "}""")).IsEqualTo("Missing or empty 'pattern'.");
        await Ok(tool, """{"pattern":"**/*.cs"}""");
    }

    [Test]
    public async Task Tree_Messages()
    {
        var tool = new TreeTool(NullLogger<TreeTool>.Instance);
        await Assert.That(await Error(tool, """{"maxDepth":0}""")).IsEqualTo("'maxDepth' must be between 1 and 10.");
        await Assert.That(await Error(tool, """{"maxDepth":11}""")).IsEqualTo("'maxDepth' must be between 1 and 10.");
        await Assert.That(await Error(tool, """{"maxEntries":10001}""")).IsEqualTo("'maxEntries' must be between 1 and 10000.");
        await Ok(tool, """{"maxDepth":"x"}""");
    }

    [Test]
    public async Task RipGrep_Messages()
    {
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'pattern'.");
        await Assert.That(await Error(tool, """{"pattern":""}""")).IsEqualTo("Missing or empty 'pattern'.");
        await Ok(tool, """{"pattern":"foo"}""");
    }

    [Test]
    public async Task Notebook_Messages()
    {
        string notesRoot = Path.Combine(Path.GetTempPath(), $"harbor-argval-{Guid.NewGuid():N}");
        var tool = new NotebookTool(NullLogger<NotebookTool>.Instance, notesRoot);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'action'.");
        await Assert.That(await Error(tool, """{"action":"bogus"}"""))
            .IsEqualTo("Unknown action 'bogus'. Valid: get, set, add, clear, list.");
        await Assert.That(await Error(tool, """{"action":"get"}""")).IsEqualTo("Action 'get' requires non-empty 'key'.");
        await Assert.That(await Error(tool, """{"action":"set","key":"k"}""")).IsEqualTo("Action 'set' requires 'content' string.");
        await Ok(tool, """{"action":"list"}""");
    }

    [Test]
    public async Task Skill_Messages()
    {
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'name'.");
        await Assert.That(await Error(tool, """{"name":"x","scope":"bogus"}"""))
            .IsEqualTo("'scope' must be 'project', 'global' or 'any'.");
        await Ok(tool, """{"name":"x","scope":"PROJECT"}""");
        await Ok(tool, """{"name":"x","scope":5}""");
    }

    [Test]
    public async Task Task_Messages()
    {
        var tool = new TaskTool(new AgentRegistry(), NullLogger<TaskTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing required argument 'agent'.");
        await Assert.That(await Error(tool, """{"agent":"a"}""")).IsEqualTo("Missing required argument 'prompt'.");
        await Assert.That(await Error(tool, """{"agent":"a","prompt":"p","background":"yes"}"""))
            .IsEqualTo("Optional argument 'background' must be a boolean.");
        await Ok(tool, """{"agent":"a","prompt":"p"}""");
    }

    [Test]
    public async Task Lsp_Messages()
    {
        var tool = new LspTool(NullLogger<LspTool>.Instance);
        await Assert.That(await Error(tool, "{}"))
            .IsEqualTo("Missing or empty 'action' (diagnostics | definition | references).");
        await Assert.That(await Error(tool, """{"action":"bogus"}"""))
            .IsEqualTo("Unknown action 'bogus' — expected diagnostics, definition or references.");
        await Assert.That(await Error(tool, """{"action":"definition","path":"f"}"""))
            .IsEqualTo("'line' (1-based) is required for action 'definition'.");
        await Assert.That(await Error(tool, """{"action":"diagnostics","path":"f","column":"x"}"""))
            .IsEqualTo("'column' must be an integer.");
        await Ok(tool, """{"action":"diagnostics","path":"f"}""");
    }

    [Test]
    public async Task McpPrompt_Messages()
    {
        var tool = new McpPromptTool(NullLogger<McpPromptTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'server'.");
        await Assert.That(await Error(tool, """{"server":"s"}""")).IsEqualTo("Missing or empty 'name'.");
        await Assert.That(await Error(tool, """{"server":"s","name":"n","arguments":[]}"""))
            .IsEqualTo("'arguments' must be a JSON object if present.");
        await Ok(tool, """{"server":"s","name":"n"}""");
    }

    [Test]
    public async Task McpResource_Messages()
    {
        var tool = new McpResourceTool(NullLogger<McpResourceTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'server'.");
        await Assert.That(await Error(tool, """{"server":"s"}""")).IsEqualTo("Missing or empty 'uri'.");
        await Ok(tool, """{"server":"s","uri":"file:///x"}""");
    }

    [Test]
    public async Task McpTool_Messages()
    {
        var tool = new McpToolTool(NullLogger<McpToolTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'server'.");
        await Assert.That(await Error(tool, """{"server":"s"}""")).IsEqualTo("Missing or empty 'method'.");
        await Assert.That(await Error(tool, """{"server":"s","method":"m","args":[]}"""))
            .IsEqualTo("'args' must be a JSON object if present.");
        await Ok(tool, """{"server":"s","method":"m"}""");
    }

    [Test]
    public async Task McpToolAdapter_Messages()
    {
        var tool = new McpToolAdapter(ScriptedMcpRegistry.Fail("unused"), "srv", "tool");
        await Assert.That(await Error(tool, """{"arguments":[]}""")).IsEqualTo("'arguments' must be a JSON object.");
        await Ok(tool, "{}");
    }

    [Test]
    public async Task WebFetch_Messages()
    {
        var tool = new WebFetchTool(NullLogger<WebFetchTool>.Instance);
        await Assert.That(await Error(tool, "{}")).IsEqualTo("Missing or empty 'url'.");
        await Assert.That(await Error(tool, """{"url":"ftp://x"}"""))
            .IsEqualTo("'url' must be an absolute http(s) URL: ftp://x");
        await Assert.That(await Error(tool, """{"url":"https://example.com","maxChars":0}"""))
            .IsEqualTo("'maxChars' must be >= 1.");
        await Ok(tool, """{"url":"https://example.com"}""");
    }
}
