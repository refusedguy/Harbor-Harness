using Harbor.Terminal.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     One-line permission footer density ([steal/opencode] #1182):
///     title + first body line + key hint on a single terminal-safe line,
///     with a mono fallback for pipes.
/// </summary>
public class PermissionMiniLineTests
{
    [Test]
    public async Task Format_FullLine_HasMarkTitleSnippetAndHint()
    {
        string line = PermissionMiniLine.Format("run shell: make build", "$ make build");

        await Assert.That(line).IsEqualTo("⚠ run shell: make build — $ make build [y/a/n]");
    }

    [Test]
    public async Task Format_Mono_UsesAsciiMarkSeparatorAndHint()
    {
        string line = PermissionMiniLine.Format("run shell: make build", "$ make build", mono: true);

        await Assert.That(line).IsEqualTo("! run shell: make build - $ make build [y/a/n]");
    }

    [Test]
    public async Task Format_NoSnippet_OmitsSeparator()
    {
        string line = PermissionMiniLine.Format("edit foo.cs", null);

        await Assert.That(line).IsEqualTo("⚠ edit foo.cs [y/a/n]");
    }

    [Test]
    public async Task Format_BlankTitle_FallsBackToQuestion()
    {
        string line = PermissionMiniLine.Format("  ", "x", mono: true);

        await Assert.That(line).IsEqualTo("! ? - x [y/a/n]");
    }

    [Test]
    public async Task Format_LongLine_TruncatesToWidthWithEllipsis()
    {
        string line = PermissionMiniLine.Format(new string('a', 100), null, 20);

        await Assert.That(line.Length).IsEqualTo(20);
        await Assert.That(line.EndsWith("…", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Format_MonoTruncation_UsesAsciiEllipsis()
    {
        string line = PermissionMiniLine.Format(new string('a', 100), null, 20, mono: true);

        await Assert.That(line.Length).IsEqualTo(20);
        await Assert.That(line.EndsWith("...", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Format_ControlChars_BecomeOneInertLine()
    {
        string line = PermissionMiniLine.Format("t\nt", "a\u001bb", mono: true);

        await Assert.That(line).IsEqualTo("! t t - ab [y/a/n]");
        await Assert.That(line.Contains('\n')).IsFalse();
    }

    [Test]
    public async Task Format_NonPositiveWidth_FallsBackToDefault()
    {
        string line = PermissionMiniLine.Format("t", null, 0);

        await Assert.That(line).IsEqualTo(PermissionMiniLine.Format("t", null, PermissionMiniLine.DefaultMaxWidth));
    }

    [Test]
    public async Task Format_Cjk_CharsFitButCellsOverflow_TruncatesToCellBudget()
    {
        // "あ" is 1 char but 2 cells: "! " + 8 CJK + " [y/a/n]" is 18 chars
        // (fits 20 by char count) but 26 cells (overflows 20 cells). A
        // char-count cut returns it whole; a cell-count cut truncates.
        string line = PermissionMiniLine.Format(new string('あ', 8), null, 20, mono: true);

        await Assert.That(line.EndsWith("...", StringComparison.Ordinal)).IsTrue();
        await Assert.That(CellWidth(line)).IsLessThanOrEqualTo(20);
    }

    [Test]
    public async Task Format_Cjk_FittingLine_NotTruncated()
    {
        string line = PermissionMiniLine.Format("あ", null, 20, mono: true);

        await Assert.That(line).IsEqualTo("! あ [y/a/n]");
    }

    [Test]
    public async Task Format_Emoji_TruncationNeverSplitsSurrogatePair()
    {
        string line = PermissionMiniLine.Format(new string('a', 16) + "😀" + new string('b', 10), null, 20, mono: true);

        await Assert.That(line.EndsWith("...", StringComparison.Ordinal)).IsTrue();
        await Assert.That(CellWidth(line)).IsLessThanOrEqualTo(20);
        await Assert.That(HasUnpairedSurrogate(line)).IsFalse();
    }

    private static int CellWidth(string line)
    {
        int cells = 0;
        var span = line.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            if (System.Text.Rune.DecodeFromUtf16(span[i..], out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                consumed = 1;
                cells += 1;
                i += consumed;
                continue;
            }

            int v = rune.Value;
            cells += v >= 0x10000 || v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 ? 2 : 1;
            i += consumed;
        }

        return cells;
    }

    private static bool HasUnpairedSurrogate(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (char.IsHighSurrogate(line[i]))
            {
                if (i + 1 >= line.Length || !char.IsLowSurrogate(line[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(line[i]))
            {
                return true;
            }
        }

        return false;
    }
}
