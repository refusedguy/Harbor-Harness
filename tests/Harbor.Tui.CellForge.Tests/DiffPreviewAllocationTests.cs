using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Allocation-budget coverage for the inline diff-preview path (#186,
/// CF-E-011 follow-up). The non-diff-tool guard returns before any parse and
/// is asserted fully allocation-free; the <c>edit</c> extraction (single
/// <c>JsonDocument</c> pass + two bounded diff generations) is a generous,
/// CI-safe tripwire in the <c>SpanParserTests</c> tradition.
/// </summary>
public class DiffPreviewAllocationTests
{
    [Test]
    public async Task ExtractDiff_NonDiffTool_IsAllocationFree()
    {
        for (int i = 0; i < 1_000; i++)
        {
            _ = DiffPreview.ExtractDiff("read", """{"path":"src/a.cs"}""", resultText: null);
        }

        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();

        const int calls = 2_000;
        for (int i = 0; i < calls; i++)
        {
            _ = DiffPreview.ExtractDiff("read", """{"path":"src/a.cs"}""", resultText: null);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task ExtractDiff_Edit_StaysBounded()
    {
        const string args = """{"path":"src/a.cs","oldString":"a\nb\nc","newString":"a\nB\nc"}""";

        var first = DiffPreview.ExtractDiff("edit", args, resultText: null);
        await Assert.That(first.IsDiffTool).IsTrue();
        await Assert.That(first.FilePath).IsEqualTo("src/a.cs");

        for (int i = 0; i < 200; i++)
        {
            _ = DiffPreview.ExtractDiff("edit", args, resultText: null);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int calls = 300;
        for (int i = 0; i < calls; i++)
        {
            _ = DiffPreview.ExtractDiff("edit", args, resultText: null);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"diffpreview-alloc: edit ExtractDiff avg = {(double)allocated / calls:F0} B over {calls} calls");
        await Assert.That(allocated).IsLessThanOrEqualTo(calls * 32L * 1_024L);
    }
}
