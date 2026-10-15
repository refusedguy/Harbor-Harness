using System.Reflection;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1110 mirror-poison moat: <see cref="DiffEngine.Diff"/> stays out of the
/// public surface so no out-of-assembly caller can open a diff and abandon it
/// half-drained (FRONT half-advanced, terminal mirror poisoned, no error).
/// The only sanctioned drain is <c>Flush</c>; tests reach <c>Diff</c> through
/// InternalsVisibleTo, which the existing BufferDiff/FrameDiff suites prove
/// by calling it directly.
/// </summary>
public class FrameDiffVisibilityTests
{
    [Test]
    public async Task Diff_IsNotPublic_UnderdrainCannotEscapeAssembly()
    {
        var method = typeof(DiffEngine).GetMethod("Diff", BindingFlags.Public | BindingFlags.Instance);
        await Assert.That(method).IsNull();
    }
}
