using System.Text;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Rendering;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #2704 P01. A property fuzz over the layout solver: any tree built from
///     random splits, gaps and panels must solve to rects that stay inside the
///     viewport. The generator is seeded through <see cref="TestSeeds" />, so a
///     red CI run names the seed that produced the tree.
/// </summary>
/// <remarks>
///     The invariant is about what the solver returns, not how it computes:
///     a caller can paint a frame from every panel rect without clipping or
///     wrapping. Collapse is a legitimate result (a collapsed panel reads a
///     default rect), but an out-of-bounds rect never is.
/// </remarks>
public class LayoutTreeFamilyFuzzTests
{
    private const int SampleCount = 50;

    // Large, medium and cramped: the last one forces the priority collapse
    // path, where a loser subtree must read default rects rather than keep a
    // stale rectangle from a previous, larger solve.
    private static readonly (int Width, int Height)[] Viewports =
    [
        (80, 24),
        (24, 9),
        (7, 3),
    ];

    [Test]
    public async Task Solve_AnyGeneratedTree_KeepsEveryPanelInsideViewport()
    {
        var violations = new List<string>();
        int[] defaults = Enumerable.Range(20261119, SampleCount).ToArray();

        foreach (int seed in TestSeeds.Values(defaults))
        {
            CheckSeed(seed, violations);
        }

        await Assert.That(violations).IsEmpty().Because(string.Join("\n", violations.Take(10)));
    }

    private static void CheckSeed(int seed, List<string> violations)
    {
        (LayoutTree tree, string shape) = Sample(seed);

        foreach ((int width, int height) in Viewports)
        {
            // Termination is part of the contract: if Solve ever loops, the
            // run never reaches the assertions below.
            tree.Solve(width, height);

            foreach (Panel panel in tree.Panels)
            {
                var r = panel.Rect;
                bool inside = r.X >= 0
                              && r.Y >= 0
                              && r.Width >= 0
                              && r.Height >= 0
                              && (long)r.X + r.Width <= width
                              && (long)r.Y + r.Height <= height;
                if (inside)
                {
                    continue;
                }

                violations.Add(
                    $"seed {seed}, viewport {width}x{height}, panel '{panel.Id}' " +
                    $"rect (x={r.X}, y={r.Y}, w={r.Width}, h={r.Height}), shape: {shape}");
            }
        }
    }

    /// <summary>
    ///     Builds one arbitrary tree. Every leaf is a <see cref="StubPanel" />
    ///     with a small minimum, so an over-constrained cramped viewport can
    ///     collapse safely but can never demand a negative extent.
    /// </summary>
    private static (LayoutTree Tree, string Shape) Sample(int seed)
    {
        var rng = new Random(seed);
        var tree = new LayoutTree();
        int nextId = 0;
        var shape = new StringBuilder();

        Panel root = NewPanel(ref nextId, rng);
        tree.AddRoot(root);
        var leaves = new List<string> { root.Id };
        shape.Append("root ").Append(root.Id);

        int mutations = rng.Next(3, 9);
        for (int step = 0; step < mutations && leaves.Count > 0; step++)
        {
            int targetIndex = rng.Next(leaves.Count);
            string target = leaves[targetIndex];
            leaves.RemoveAt(targetIndex);
            var dir = rng.Next(2) == 0 ? SplitDir.Horizontal : SplitDir.Vertical;
            byte gap = (byte)rng.Next(0, 3);

            if (rng.Next(4) == 0)
            {
                int count = rng.Next(1, 4);
                var added = new List<Panel>(count);
                for (int i = 0; i < count; i++)
                {
                    added.Add(NewPanel(ref nextId, rng));
                }

                if (dir == SplitDir.Horizontal)
                {
                    tree.AddRow(target, added, gap);
                }
                else
                {
                    tree.AddColumn(target, added, gap);
                }

                foreach (Panel panel in added)
                {
                    leaves.Add(panel.Id);
                }

                shape.Append($" | stack {target}/{dir}/{count}/g{gap}");
            }
            else
            {
                float ratio = 0.15f + ((float)rng.NextDouble() * 0.7f);
                Panel added = NewPanel(ref nextId, rng);
                tree.Split(target, dir, ratio, added, gap);
                leaves.Add(added.Id);
                shape.Append($" | split {target}/{dir}/{ratio:F2}/g{gap}");
            }
        }

        return (tree, shape.ToString());
    }

    private static StubPanel NewPanel(ref int nextId, Random rng) =>
        new($"p{nextId++}", rng.Next(1, 3), rng.Next(1, 3), rng.Next(0, 4));

    private sealed class StubPanel(string id, int minW, int minH, int priority = 0)
        : Panel(id, new Size(minW, minH), priority)
    {
        public override void Paint(ScreenBuffer buffer) { }
    }
}
