using System.Text.Json;
using Harbor.Abstractions.Lsp;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for the LSP-aware read/edit hooks: reads auto-open supported
///     files in the language server, edits push changes and summarize fresh
///     diagnostics. Without a registered <see cref="ILspService" /> both tools
///     behave exactly as before.
/// </summary>
public class LspHookTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harbor-lsp-hooks").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task Read_SupportedFile_OpensInLanguageServer()
    {
        string path = WriteFile("a.cs", "class A { }\n");
        var lsp = new RecordingLspService();
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)}}}").RootElement,
            CreateContext(RecordingLspService.ServicesWith(lsp)));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(lsp.Opened).Contains(path);
    }

    [Test]
    public async Task Read_UnsupportedFile_SkipsLanguageServer()
    {
        string path = WriteFile("notes.txt", "hello\n");
        var lsp = new RecordingLspService();
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)}}}").RootElement,
            CreateContext(RecordingLspService.ServicesWith(lsp)));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(lsp.Opened).IsEmpty();
    }

    [Test]
    public async Task Read_NoLspService_OutputUnchanged()
    {
        string path = WriteFile("b.cs", "class B { }\n");
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)}}}").RootElement,
            CreateContext(null!));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("class B");
    }

    [Test]
    public async Task Edit_SupportedFile_NotifiesChange_AndSummarizesDiagnostics()
    {
        string path = WriteFile("c.cs", "class C { }\n");
        var lsp = new RecordingLspService();
        lsp.DiagnosticsToReturn.Add(new LspDiagnostic(path, 0, 0, 0, 5, LspSeverity.Error, "cs", "boom"));
        lsp.DiagnosticsToReturn.Add(new LspDiagnostic(path, 1, 0, 1, 5, LspSeverity.Warning, "cs", "hmm"));
        var tool = new EditTool(NullLogger<EditTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)},\"oldString\":\"class C\",\"newString\":\"class C2\"}}").RootElement,
            CreateContext(RecordingLspService.ServicesWith(lsp)));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(lsp.Changed.Count).IsEqualTo(1);
        await Assert.That(lsp.Changed[0].Path).IsEqualTo(path);
        await Assert.That(result.Output).Contains("replacement(s)");
        await Assert.That(result.Output).Contains("LSP: 2 diagnostic(s) (1 error(s), 1 warning(s))");
    }

    [Test]
    public async Task Edit_NoDiagnostics_OutputHasNoLspNote()
    {
        string path = WriteFile("d.cs", "class D { }\n");
        var lsp = new RecordingLspService();
        var tool = new EditTool(NullLogger<EditTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)},\"oldString\":\"class D\",\"newString\":\"class D2\"}}").RootElement,
            CreateContext(RecordingLspService.ServicesWith(lsp)));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).DoesNotContain("LSP:");
        await Assert.That(lsp.Changed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Edit_UnsupportedFile_SkipsLanguageServer()
    {
        string path = WriteFile("e.txt", "a\n");
        var lsp = new RecordingLspService();
        var tool = new EditTool(NullLogger<EditTool>.Instance);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse($"{{\"path\":{JsonSerializer.Serialize(path)},\"oldString\":\"a\",\"newString\":\"b\"}}").RootElement,
            CreateContext(RecordingLspService.ServicesWith(lsp)));

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(lsp.Changed).IsEmpty();
        await Assert.That(result.Output).DoesNotContain("LSP:");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ToolContext CreateContext(IServiceProvider services) => new(
        "test-session",
        "test-message",
        "test-call",
        "code",
        CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        services);
}
