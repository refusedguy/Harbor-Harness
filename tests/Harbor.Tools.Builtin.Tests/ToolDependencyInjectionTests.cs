using System.Text.Json;
using Harbor.Abstractions.Lsp;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #470 — the seven tools that used to resolve their dependencies from
///     <c>ToolContext.Services</c> now take them through the constructor. A
///     <see cref="ToolContext" /> is therefore enough to run any of them, and an
///     absent dependency produces a tool error, never a
///     <see cref="NullReferenceException" /> and never a lookup against a provider
///     the agent loop passed as <c>null!</c>.
/// </summary>
public class ToolDependencyInjectionTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("harbor-tool-di").FullName;

    private static ToolContext Ctx() => new(
        "s1",
        "m1",
        "c1",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── read / edit: ILspService ──────────────────────────────────────────

    [Test]
    public async Task Read_WithoutLsp_ReadsTheFile_AndNeverThrows()
    {
        string path = WriteFile("plain.cs", "class Plain { }\n");
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}}}"""), Ctx());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("class Plain");
    }

    [Test]
    public async Task Edit_WithoutLsp_AppliesTheEdit_AndOmitsTheDiagnosticsNote()
    {
        string path = WriteFile("plain2.cs", "class Old { }\n");
        var tool = new EditTool(NullLogger<EditTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}},"oldString":"Old","newString":"New"}"""), Ctx());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).DoesNotContain("LSP:");
    }

    [Test]
    public async Task Read_WithInjectedLsp_UsesItForTheAutoOpenHook()
    {
        string path = WriteFile("hooked.cs", "class Hooked { }\n");
        var lsp = new RecordingLspService();
        var tool = new ReadTool(NullLogger<ReadTool>.Instance, lsp);

        ToolResult result = await tool.ExecuteAsync(
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}}}"""), Ctx());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(lsp.Opened).Contains(path);
    }

    [Test]
    public async Task Edit_WithInjectedLsp_UsesItForTheDiagnosticsNote()
    {
        string path = WriteFile("noted.cs", "class Noted { }\n");
        var lsp = new RecordingLspService();
        lsp.DiagnosticsToReturn.Add(new LspDiagnostic(path, 0, 0, 0, 3, LspSeverity.Error, "cs", "boom"));
        var tool = new EditTool(NullLogger<EditTool>.Instance, lsp);

        ToolResult result = await tool.ExecuteAsync(
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}},"oldString":"Noted","newString":"Noted2"}"""), Ctx());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("LSP: 1 diagnostic(s)");
    }

    // ── mcp family: IMcpRegistry ──────────────────────────────────────────

    [Test]
    [Arguments("mcp")]
    [Arguments("read_mcp_resource")]
    [Arguments("mcp_prompt")]
    public async Task McpTools_WithoutRegistry_ReturnAnActionableError(string toolName)
    {
        ITool tool = toolName switch
        {
            "mcp" => new McpToolTool(NullLogger<McpToolTool>.Instance),
            "read_mcp_resource" => new McpResourceTool(NullLogger<McpResourceTool>.Instance),
            _ => new McpPromptTool(NullLogger<McpPromptTool>.Instance),
        };
        string json = toolName switch
        {
            "mcp" => """{"server":"fs","method":"tools/list"}""",
            "read_mcp_resource" => """{"server":"fs","uri":"file:///x"}""",
            _ => """{"server":"fs","name":"p"}""",
        };

        ToolResult result = await tool.ExecuteAsync(Args(json), Ctx());

        await Assert.That(result.IsError).IsTrue();
        // Points at the composition root, not at a container the caller never had.
        await Assert.That(result.Output).Contains("composition root");
    }

    // ── lsp: ILspService ──────────────────────────────────────────────────

    [Test]
    public async Task LspTool_WithoutService_ReportsTheMissingConstructorDependency()
    {
        var tool = new LspTool(NullLogger<LspTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(
            Args("""{"action":"diagnostics","path":"/tmp/a.cs"}"""), Ctx());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("injected into the 'lsp' tool");
    }

    // ── skill: ISessionStore ──────────────────────────────────────────────

    [Test]
    public async Task SkillTool_WithoutStore_FallsBackToTheProcessWorkingDirectory()
    {
        // The old code asked the (always null) ToolContext provider for an
        // ISessionStore here; now the field is simply null and the roots fall
        // back to the process CWD. An unknown skill is a tool error, not a crash.
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);

        ToolResult result = await tool.ExecuteAsync(Args("""{"name":"no-such-skill"}"""), Ctx());

        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task SkillTool_WithInjectedStore_ResolvesTheSessionDirectory()
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var store = new SingleSessionStore(dir);
        var tool = new SkillTool(store, NullLogger<SkillTool>.Instance);

        // The store's directory becomes the project skills root, so a skill
        // planted there is found — proof the injected store is actually consulted.
        string skillDir = Path.Combine(dir, ".harbor", "skills", "review");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# From the session directory");

        ToolResult result = await tool.ExecuteAsync(Args("""{"name":"review"}"""), Ctx());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("From the session directory");
    }

    /// <summary>Minimal store answering exactly the one call <c>SkillTool</c> makes.</summary>
    private sealed class SingleSessionStore : ISessionStore
    {
        private readonly Session _session;

        public SingleSessionStore(string directory) =>
            _session = Session.Create(directory, "code", "kilocode", "kilo-auto", "s1");

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(_session));

        public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
