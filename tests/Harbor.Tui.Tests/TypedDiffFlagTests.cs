using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Terminal.Abstractions.ViewModels;
namespace Harbor.Tui.Tests;

/// <summary>
///     #1100, the slice after #2303: the diff preview's per-completion
///     trigger is the typed <see cref="ToolResultEffect"/> on the result,
///     not a scan of the output text. These tests pin the boundary the
///     consumer actually sees: a declared file change is recorded, and a
///     result that only looks like one in prose is not.
/// </summary>
public class TypedDiffFlagTests
{
    private static ToolExecutionEndEvent End(string output, ToolResultEffect effect, bool isError = false) =>
        new("tc-1", new ToolResult(output, isError) { Effect = effect }, isError);

    [Test]
    public async Task NewToolResult_DefaultsToNoEffect()
    {
        var result = new ToolResult("anything", false);

        await Assert.That(result.Effect).IsEqualTo(ToolResultEffect.None);
    }

    [Test]
    public async Task DeclaredFileChange_AddsOneDiff_KeepingTheOutput()
    {
        var vm = new DiffPreviewViewModel();

        await vm.UpdateFromEventAsync(End("created src/app.cs", ToolResultEffect.FileChange));

        await Assert.That(vm.Diffs.Count).IsEqualTo(1);
        await Assert.That(vm.Diffs[0].Output).IsEqualTo("created src/app.cs");
    }

    [Test]
    public async Task LegacyWroteText_WithoutFlag_DoesNotAddDiff()
    {
        var vm = new DiffPreviewViewModel();

        await vm.UpdateFromEventAsync(End("Wrote 100 chars to /tmp/test.cs", ToolResultEffect.None));

        await Assert.That(vm.Diffs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LegacyEditedText_WithoutFlag_DoesNotAddDiff()
    {
        var vm = new DiffPreviewViewModel();

        await vm.UpdateFromEventAsync(End("Edited /tmp/test.cs: 1 replacement(s)", ToolResultEffect.None));

        await Assert.That(vm.Diffs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeclaredFileChange_OnErrorResult_DoesNotAddDiff()
    {
        var vm = new DiffPreviewViewModel();

        await vm.UpdateFromEventAsync(End("Edited /tmp/test.cs", ToolResultEffect.FileChange, isError: true));

        await Assert.That(vm.Diffs.Count).IsEqualTo(0);
    }
}
