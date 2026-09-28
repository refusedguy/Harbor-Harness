using BenchmarkDotNet.Attributes;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks <see cref=\"PatchTool\" /> unified-diff parsing and application.
///     Measures the cost of parsing a large patch (5000 hunts) and applying
///     it to a target buffer, focusing on zero-allocation span-based line
///     splitting and context matching.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> one full <c>PatchTool.ExecuteAsync</c> (parse + apply a
///          <c>HunkCount</c>-hunk unified diff to a temp file), or a hand-rolled parse-only
///          scan of the same patch.</item>
///          <item><c>Payload:</c> a synthetic C# file of <c>HunkCount × 10</c> lines plus its
///          unified diff, both built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per iteration — <c>ResetTargetFile</c> rewrites the
///          target file with the original content before every iteration (the tool mutates
///          it), and <c>Cleanup</c> deletes it afterwards, so every iteration applies the same
///          patch to the same input.</item>
///          <item><c>Drain:</c> none — the tool writes the file synchronously; nothing is
///          queued.</item>
///          <item><c>RetainedState:</c> the tool instance (stateless between calls) and the
///          immutable patch string; the temp file itself is reset, not retained.</item>
///          <item><c>AwaitSemantics:</c> the apply row awaits the tool execution, so the file
///          read + rewrite is inside the measurement; the parse row is synchronous.</item>
///          <item><c>AllocAttribution:</c> line splitting + hunk/context buffers + the
///          rewritten file content. The apply row additionally pays the <c>JsonSerializer</c>
///          roundtrip used to build the tool arguments.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PatchToolUnifiedDiffBenchmark
{
    private PatchTool _tool = null!;
    private string _originalFile = null!;
    private string _patch = null!;
    private string _tempFilePath = null!;

    [Params(100, 1000, 5000)]
    public int HunkCount;

    [GlobalSetup]
    public void Setup()
    {
        _tool = new PatchTool(NullLogger<PatchTool>.Instance);
        _originalFile = BuildOriginalFile(HunkCount * 10);
        _patch = BuildUnifiedDiff(_originalFile, HunkCount);
        _tempFilePath = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllText(_tempFilePath, _originalFile);
    }

    [IterationSetup]
    public void ResetTargetFile()
    {
        // ApplyPatch mutates the target file; restore the original content per
        // iteration so every iteration applies the same patch to the same input
        // (IterationCleanup deletes the file, WriteAllText recreates it).
        System.IO.File.WriteAllText(_tempFilePath, _originalFile);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        try { System.IO.File.Delete(_tempFilePath); } catch { }
    }

    [Benchmark(Description = "Parse + Apply unified diff (N hunks)", Baseline = true)]
    public async Task<string> ApplyPatch()
    {
        var args = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { path = _tempFilePath, patch = _patch })).RootElement.Clone();
        var ctx = new ToolContext(
            SessionId: "session-1",
            MessageId: "msg-1",
            CallId: null,
            Agent: "code",
            Abort: CancellationToken.None,
            Messages: Array.Empty<AgentMessage>(),
            ReportProgress: (_, __) => Task.CompletedTask,
            Ask: (_, __) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
            Services: null!);
        var result = await _tool.ExecuteAsync(args, ctx).ConfigureAwait(false);
        return result.Output;
    }

    [Benchmark(Description = "Parse unified diff only")]
    public List<object> ParseDiffOnly()
    {
        var lines = _patch.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var hunks = new List<object>();
        int i = 0;
        while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            i++;
        while (i < lines.Length)
        {
            if (lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                hunks.Add(lines[i]);
                i++;
                while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
                {
                    if (lines[i].Length > 0 && lines[i][0] is ' ' or '+' or '-')
                        hunks.Add(lines[i]);
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
        return hunks;
    }

    private static string BuildOriginalFile(int lineCount)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lineCount; i++)
        {
            sb.AppendLine($"public class Class{i} {{");
            sb.AppendLine($"    public void Method{i}() {{ /* original */ }}");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string BuildUnifiedDiff(string original, int hunkCount)
    {
        var lines = original.Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- a/file.cs");
        sb.AppendLine("+++ b/file.cs");

        int linesPerHunk = Math.Max(1, lines.Length / hunkCount);
        for (int h = 0; h < hunkCount; h++)
        {
            int start = h * linesPerHunk;
            if (start >= lines.Length) break;

            sb.AppendLine($"@@ -{start + 1},3 +{start + 1},4 @@");
            sb.AppendLine(" " + lines[start]);
            sb.AppendLine("-    public void Method" + start + "() { /* original */ }");
            sb.AppendLine("+    public void Method" + start + "() { /* updated */ }");
            sb.AppendLine(" " + lines[start + 1] ?? "");
        }

        return sb.ToString();
    }
}
