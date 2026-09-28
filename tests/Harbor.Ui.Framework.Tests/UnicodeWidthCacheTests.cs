using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Tests;

/// <summary>ENG5 (issue #276): per-run width memoization matches the uncached table.</summary>
public class UnicodeWidthCacheTests
{
    [Test]
    public async Task Cached_Matches_Uncached_For_Representative_Runs()
    {
        string[] runs =
        [
            string.Empty,
            "hello",
            "$0.0000",
            "142↑ 87↓",
            "→ tool_name",
            "⠋", // spinner braille — single cell
            "◐", // awaiting ring — single cell
            "日本語テスト", // wide runes — 2 cells each
            "e\u0301", // combining mark — zero width
            "a\u200bb", // zero-width space
            "🙂", // emoji — wide
            "mix: a日本b\u0301c 🙂 $1.23",
        ];

        foreach (var run in runs)
        {
            int expected = UnicodeWidth.Width(run.AsSpan());
            await Assert.That(UnicodeWidth.WidthCached(run)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Repeated_Lookups_Are_Stable()
    {
        const string run = "kilocode/kilo-auto/free | agent: code";
        int first = UnicodeWidth.WidthCached(run);
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(UnicodeWidth.WidthCached(run)).IsEqualTo(first);
        }

        await Assert.That(first).IsEqualTo(UnicodeWidth.Width(run.AsSpan()));
    }

    [Test]
    public async Task Many_Distinct_Runs_Stay_Correct()
    {
        // More uniques than cache slots — collisions evict, never corrupt.
        for (int i = 0; i < 1_000; i++)
        {
            string run = $"segment-{i} ↑↓";
            await Assert.That(UnicodeWidth.WidthCached(run)).IsEqualTo(UnicodeWidth.Width(run.AsSpan()));
        }
    }
}
