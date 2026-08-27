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
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
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
