using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Issue #482: the diff-preview path shortener computed a from-end slice
///     length of <c>max - file.Length - 4</c>, which reaches <c>-1</c> once a
///     long file name eats the whole row (<c>file.Length == max - 3</c> passes
///     the boundary check). <c>dir[^(-1)..]</c> then builds an
///     <see cref="Index" /> from a negative value and throws
///     <see cref="ArgumentOutOfRangeException" />. The slice length is now
///     clamped to 0, which degrades to the budgeted "…" + file-name form.
///     Rendering is byte-identical for every input the old code survived
///     (verified exhaustively), so no golden baseline moves.
/// </summary>
public class PanelRowsPathTests
{
    /// <summary>DiffRows spends 4 leading columns (icon, space, ok, space).</summary>
    private const int Prefix = 4;

    private static string PathRow(string filePath, int width)
    {
        List<string> rows = PanelRows.DiffRows(
            [new PanelFileChange("edit", filePath, string.Empty, false)], width);

        return rows[^1][Prefix..];
    }

    [Test]
    public async Task ShortPath_ZeroDirectoryBudget_KeepsFileName()
    {
        // width 18 → max 6; file "/xy" (3) → budget 0 → clamped keep 0.
        string path = PathRow("abcdef/xy", 18);

        await Assert.That(path).IsEqualTo("…/xy");
    }

    [Test]
    public async Task ShortPath_ZeroDirectoryBudget_DeepDirectory()
    {
        string path = PathRow("deep/dir/ab", 18);

        await Assert.That(path).IsEqualTo("…/ab");
    }

    [Test]
    public async Task ShortPath_UnitDirectoryBudget_KeepsFileName()
    {
        // budget 1 → keep 0 → "…" + the file name, unchanged by the fix.
        string path = PathRow("abc/de/x", 18);

        await Assert.That(path).IsEqualTo("…/x");
    }

    [Test]
    public async Task ShortPath_WindowsSeparator_ZeroBudget()
    {
        string path = PathRow("src\\abc\\def\\ab", 18);

        await Assert.That(path).IsEqualTo("…\\ab");
    }

    [Test]
    public async Task ShortPath_LongDirectory_KeepsTailAndFileName()
    {
        // width 40 → max 28; file 24 → budget 1 → "…" + one directory char.
        string path = PathRow("src/a/very/deep/dir/very-long-file-name.cs", 40);

        await Assert.That(path).IsEqualTo("…r/very-long-file-name.cs");
    }

    [Test]
    public async Task ShortPath_MultiSegmentDirectory_KeepsTail()
    {
        string path = PathRow("src/a/aaaaaaaaaaaaaa/bbbbbbbbbbbbbbbbb/cccccccccccccc/file.cs", 44);

        await Assert.That(path).IsEqualTo("…bbbbb/cccccccccccccc/file.cs");
    }

    [Test]
    public async Task ShortPath_NoSeparator_UsesMiddleTruncation()
    {
        // No slash → the head/tail fallback, untouched by the directory budget.
        string path = PathRow("no-separator-file.cs", 18);

        await Assert.That(path).IsEqualTo("l…s");
    }

    [Test]
    public async Task ShortPath_ShortPath_PassesThrough()
    {
        string path = PathRow("a/b", 18);

        await Assert.That(path).IsEqualTo("a/b");
    }

    [Test]
    [Arguments("")]
    [Arguments("x")]
    [Arguments("/")]
    [Arguments("a//b")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("very/deep/nested/dir/tree/file-with-a-long-name.cs")]
    [Arguments("src\\Harbor.Core\\Some\\Deep\\Path\\File.cs")]
    public async Task ShortPath_AcrossPaths_NeverThrowsAndFitsWidth(string file)
    {
        foreach (int width in new[] { 16, 18, 20, 24, 40, 64, 80 })
        {
            string row = PanelRows.DiffRows(
                [new PanelFileChange("edit", file, string.Empty, false)], width)[^1];

            await Assert.That(row.Length).IsLessThanOrEqualTo(width);
        }
    }
}
