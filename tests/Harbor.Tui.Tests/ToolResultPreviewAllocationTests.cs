using System.Runtime.CompilerServices;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
namespace Harbor.Tui.Tests;

/// <summary>
///     #1100 — allocation gate for the per-tool-end transcript preview.
///     <para>
///         <see cref="ChatHistoryViewModel.BuildToolResultPreview"/> replaced
///         the inline <c>Output[..200] + "..."</c> expression, which allocated
///         a 200-char substring and then a 203-char concatenation for every
///         long tool result. This gate measures the exact production method
///         and proves it now builds the same text in ONE allocation, while a
///         control running the old two-allocation shape fails the same gate.
///     </para>
/// </summary>
public class ToolResultPreviewAllocationTests
{
    // Large enough that tiered JIT settles during warmup, small enough that
    // the whole window stays well under a millisecond and cannot be skewed
    // by a GC or a scheduler preemption (thread-local allocation counter only
    // counts allocations made by THIS thread inside the window).
    private const int Iterations = 4096;
    private const int ThresholdBytesPerCall = 520;

    /// <summary>
    ///     The removed expression, kept ONLY as a sensitivity control: the
    ///     gate must be able to fail on the shape this slice replaced, or it
    ///     proves nothing about the current one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string OldPreview(string output) =>
        output.Length > ChatHistoryViewModel.PreviewMaxChars
            ? output[..ChatHistoryViewModel.PreviewMaxChars] + "..."
            : output;

    /// <summary>
    ///     Warm up, then measure the bytes this thread allocates per call.
    ///     The delegate is built once outside the window; delegate invoke
    ///     itself does not allocate.
    /// </summary>
    private static double AllocatedPerCall(string output, Func<string, string> build)
    {
        for (int i = 0; i < Iterations; i++) _ = build(output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string? sink = null;
        for (int i = 0; i < Iterations; i++) sink = build(output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(sink);

        return (double)allocated / Iterations;
    }

    [Test]
    public async Task BuildToolResultPreview_ShortOutput_ReturnsSameInstance()
    {
        const string output = "Wrote file.txt (12 chars)";

        string preview = ChatHistoryViewModel.BuildToolResultPreview(output);

        await Assert.That(ReferenceEquals(preview, output)).IsTrue();
        await Assert.That(preview).IsEqualTo(output);
    }

    [Test]
    public async Task BuildToolResultPreview_ExactlyAtBudget_ReturnsSameInstance()
    {
        string output = new('x', ChatHistoryViewModel.PreviewMaxChars);

        string preview = ChatHistoryViewModel.BuildToolResultPreview(output);

        await Assert.That(ReferenceEquals(preview, output)).IsTrue();
        await Assert.That(preview.Length).IsEqualTo(ChatHistoryViewModel.PreviewMaxChars);
    }

    [Test]
    public async Task BuildToolResultPreview_LongOutput_IsFirst200CharsPlusEllipsis()
    {
        string output = new string('x', ChatHistoryViewModel.PreviewMaxChars - 3) + "TAIL" + new string('z', 500);

        string preview = ChatHistoryViewModel.BuildToolResultPreview(output);

        await Assert.That(preview).IsEqualTo(output[..ChatHistoryViewModel.PreviewMaxChars] + "...");
        await Assert.That(preview.Length).IsEqualTo(ChatHistoryViewModel.PreviewMaxChars + 3);
    }

    [Test]
    public async Task ToolExecutionEnd_PreviewContent_IsUnchanged()
    {
        var vm = new ChatHistoryViewModel();
        string output = new string('a', ChatHistoryViewModel.PreviewMaxChars) + "TRUNCATED";
        var result = new ToolResult(output, IsError: false);
        var evt = new ToolExecutionEndEvent("tc-1", result, IsError: false);

        await vm.UpdateFromEventAsync(evt);

        await Assert.That(vm.Entries.Count).IsEqualTo(1);
        await Assert.That(vm.Entries[0].Content).IsEqualTo("✓ " + output[..ChatHistoryViewModel.PreviewMaxChars] + "...");
    }

    [Test]
    public async Task BuildToolResultPreview_LongOutput_SingleAllocationGate()
    {
        string output = new('x', 1000);
        Func<string, string> current = ChatHistoryViewModel.BuildToolResultPreview;
        Func<string, string> old = OldPreview;

        double currentBytes = AllocatedPerCall(output, current);
        double oldBytes = AllocatedPerCall(output, old);

        // One 203-char string is 432 B on 64-bit (24 B header + 2*203, aligned).
        // 520 leaves headroom for a different object header without ever
        // admitting the old two-string shape (~856 B).
        await Assert.That(currentBytes).IsLessThan(ThresholdBytesPerCall).Because(
            "the new preview must build in ONE allocation; the old two-string shape was ~856 B/call");
        // Sensitivity: the gate fails on the code this slice removed.
        await Assert.That(oldBytes).IsGreaterThan(currentBytes * 1.5).Because(
            "the control proves the gate would fail on the two-allocation expression this slice replaced");
    }
}
