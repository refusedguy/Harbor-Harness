using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Rendering reads global TerminalColorPalette — serialized vs theme tests.
[NotInParallel("pty")]
public class TreeDepthGuardTests
{
    [Test]
    public async Task DeepChain_CappedAtMaxDepth()
    {
        TreeNode leaf = new("leaf");
        for (int i = 0; i < TreeView.MaxDepth + 20; i++)
        {
            leaf = new TreeNode("n" + i, [leaf], expanded: true);
        }

        var tree = new TreeView([leaf]);
        await Assert.That(tree.VisibleCount).IsEqualTo(TreeView.MaxDepth + 1);
    }
}
