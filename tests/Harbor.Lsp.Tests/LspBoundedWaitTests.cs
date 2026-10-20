using CSharpFunctionalExtensions;
using Harbor.Abstractions.Lsp;
using Harbor.Lsp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Lsp.Tests;

/// <summary>
///     A session operation that never gets an answer must FAIL, not wait
///     forever: the difference between a red build and a hung job.
/// </summary>
[NotInParallel("lsp")]
public class LspSessionBoundedWaitTests
{
    /// <summary>
    ///     A server that completes the handshake and then goes silent: it never
    ///     answers <c>textDocument/definition</c>.
    /// </summary>
    private const string StalledServerScript = """
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
            elif method == "shutdown":
                write_frame({"jsonrpc": "2.0", "id": mid, "result": None})
            elif method == "exit":
                sys.exit(0)
            # textDocument/definition is deliberately NOT answered: the server stalls.
        """;

    [Test]
    [Timeout(30_000)]
    [SkipUnlessPython3]
    public async Task StalledServer_Lookup_Fails_Instead_Of_Hanging()
    {
        string scriptPath = Harbor.TestKit.TestTempDirs.NewFilePath("harbor-stalled-lsp", ".py");
        string workspace = Harbor.TestKit.TestTempDirs.NewDirectory("harbor-stalled-lsp-ws");
        Directory.CreateDirectory(workspace);
        string filePath = Path.Combine(workspace, "a.ts");
        await File.WriteAllTextAsync(scriptPath, StalledServerScript);

        try
        {
            var definition = new LspServerDefinition("stalled-ts", "StalledTS", "python3", [scriptPath], [".ts"]);

            // A short budget so the test does not wait out the real 15s default.
            await using LspServerSession session = await LspServerSession.StartAsync(
                definition, workspace, NullLogger.Instance, CancellationToken.None, TimeSpan.FromMilliseconds(500));

            // didOpen is a notification: there is no answer to wait for, so it
            // completes even against a server that has gone silent.
            Result opened = await session.OpenAsync(filePath, "const x = 1;", "typescript");
            await Assert.That(opened.IsSuccess).IsTrue();

            // The lookup has no answer coming: it must fail with a timeout
            // instead of waiting for a reply that will never arrive.
            Result<Maybe<LspLocation>> found = await session.FindDefinitionAsync(filePath, 1, 0);
            await Assert.That(found.IsFailure).IsTrue();
            await Assert.That(found.Error).Contains("request-timeout");
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
