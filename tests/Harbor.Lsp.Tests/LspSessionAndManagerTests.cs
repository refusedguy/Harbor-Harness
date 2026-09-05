using System.Diagnostics;
using Harbor.Abstractions.Lsp;
using Harbor.Lsp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>Skips unless python3 is on PATH (drives the fake language server).</summary>
internal sealed class SkipUnlessPython3Attribute : SkipAttribute
{
    public SkipUnlessPython3Attribute()
        : base("python3 is not on PATH — fake LSP server cannot run.") { }

    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(TestPaths.FindOnPath("python3") is null);
}

internal static class TestPaths
{
    public static string? FindOnPath(string executable)
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

/// <summary>Wire-level flow over a real out-of-process fake server (python3).</summary>
[SkipUnlessPython3]
[NotInParallel]
public class LspServerSessionIntegrationTests
{
    private const string FakeServerScript = """
        import sys, json

        def read_frame():
            headers = {}
            while True:
                line = sys.stdin.buffer.readline()
                if line in (b"\r\n", b"\n", b""):
                    if not line and not headers:
                        sys.exit(0)
                    break
                k, _, v = line.decode("ascii").partition(":")
                headers[k.strip().lower()] = v.strip()
            n = int(headers["content-length"])
            return json.loads(sys.stdin.buffer.read(n))

        def write_frame(obj):
            body = json.dumps(obj).encode("utf-8")
            sys.stdout.buffer.write(("Content-Length: %d\r\n\r\n" % len(body)).encode("ascii"))
            sys.stdout.buffer.write(body)
            sys.stdout.buffer.flush()

        while True:
            msg = read_frame()
            method = msg.get("method")
            mid = msg.get("id")
            if method == "initialize":
                write_frame({"jsonrpc": "2.0", "id": mid, "result": {"capabilities": {}}})
            elif method == "textDocument/didOpen":
                uri = msg["params"]["textDocument"]["uri"]
                write_frame({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics",
                             "params": {"uri": uri, "diagnostics": [
                                 {"range": {"start": {"line": 0, "character": 0},
                                            "end": {"line": 0, "character": 4}},
                                  "severity": 1, "source": "fake", "message": "fake error"}]}})
            elif method == "textDocument/definition":
                write_frame({"jsonrpc": "2.0", "id": mid, "result": {
                    "uri": msg["params"]["textDocument"]["uri"],
                    "range": {"start": {"line": 4, "character": 2},
                              "end": {"line": 4, "character": 8}}}})
            elif method == "shutdown":
                write_frame({"jsonrpc": "2.0", "id": mid, "result": None})
            elif method == "exit":
                sys.exit(0)
        """;

    [Test]
    public async Task Open_PublishesDiagnostics_DefinitionResolves()
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"harbor-fake-lsp-{Guid.NewGuid():N}.py");
        string workspace = Path.Combine(Path.GetTempPath(), $"harbor-lsp-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        string filePath = Path.Combine(workspace, "a.ts");
        await File.WriteAllTextAsync(scriptPath, FakeServerScript);

        try
        {
            var definition = new LspServerDefinition(
                "fake-ts", "FakeTS", "python3", [scriptPath], [".ts"]);
            await using LspServerSession session = await LspServerSession.StartAsync(
                definition, workspace, NullLogger.Instance);

            // Diagnostics arrive as a server notification after didOpen.
            TaskCompletionSource<string> diagnosticsFor = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DiagnosticsChanged += (_, args) => diagnosticsFor.TrySetResult(args.FilePath);
            await session.OpenAsync(filePath, "const x = 1;", "typescript");
            string changed = await diagnosticsFor.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(changed).IsEqualTo(filePath);
            IReadOnlyList<LspDiagnostic> diagnostics = session.GetDiagnostics(filePath);
            await Assert.That(diagnostics.Count()).IsEqualTo(1);
            await Assert.That(diagnostics[0].Severity).IsEqualTo(LspSeverity.Error);
            await Assert.That(diagnostics[0].Message).IsEqualTo("fake error");

            // Go-to-definition normalizes back to a local path, 0-based.
            LspLocation? location = await session.FindDefinitionAsync(filePath, 1, 0, CancellationToken.None);
            await Assert.That(location is not null).IsTrue();
            await Assert.That(location!.FilePath).IsEqualTo(filePath);
            await Assert.That(location.Line).IsEqualTo(4);
            await Assert.That(location.Column).IsEqualTo(2);
        }
        finally
        {
            File.Delete(scriptPath);
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of a temp workspace
            }
        }
    }
}

public class LspManagerTests
{
    [Test]
    public async Task MissingServerBinary_DegradesGracefully()
    {
        var definition = new LspServerDefinition(
            "ghost", "Ghost", "harbor-no-such-lsp-binary", [], [".gst"]);
        await using var manager = new LspManager(NullLogger<LspManager>.Instance, [definition]);

        await Assert.That(manager.SupportsFile("/x/a.gst")).IsTrue();
        // Open must not throw even though the binary does not exist.
        await manager.OpenFileAsync("/x/a.gst", "text");
        LspLocation? location = await manager.FindDefinitionAsync("/x/a.gst", 0, 0, CancellationToken.None);
        await Assert.That(location).IsNull();
    }

    [Test]
    public async Task UnsupportedFile_IsNoOp()
    {
        await using var manager = new LspManager(NullLogger<LspManager>.Instance, []);
        await Assert.That(manager.SupportsFile("/x/a.wasm")).IsFalse();
        await manager.OpenFileAsync("/x/a.wasm", "text");
        IReadOnlyList<LspDiagnostic> diagnostics = await manager.GetDiagnosticsAsync("/x/a.wasm");
        await Assert.That(diagnostics).IsEmpty();
        LspLocation? location = await manager.FindDefinitionAsync("/x/a.wasm", 0, 0, CancellationToken.None);
        await Assert.That(location).IsNull();
    }
}
