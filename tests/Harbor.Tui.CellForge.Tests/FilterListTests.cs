using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Contract for the app-agnostic <see cref="FilterList{T}" /> primitive and its
/// paint half <see cref="FilterListView"/>: fuzzy ranking, incremental
/// narrowing, highlight spans, preview callback, stable selection and a
/// zero-alloc refilter over a warm <see cref="FuzzyMatcher" /> cache.
/// </summary>
/// <remarks>
/// The score cache is process-global, so this class is <c>[NotInParallel]</c>
/// for the same reason <c>FuzzyMatcherTests</c> is: the allocation test clears
/// and warms it and must not race a neighbour writing to it.
/// </remarks>
[NotInParallel]
public class FilterListTests
{
    private static readonly string[] Items =
    [
        "clear session",
        "session fork",
        "new session",
        "help",
        "git status",
    ];

    private static FilterList<string> List(Func<string, string>? previewOf = null) =>
        new(Items, static s => s, previewOf);

    [Test]
    public async Task Fuzzy_RanksMatchesByScore_AndDropsNonMatches()
    {
        var list = List();
        list.SetQuery("ses");

        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(IndexOf(list, "session fork") >= 0).IsTrue();
        await Assert.That(IndexOf(list, "help") < 0).IsTrue();
        // "session fork" starts with a boundary "s"; it ranks above "clear session".
        await Assert.That(list.Results[0]).IsEqualTo("session fork");
    }

    [Test]
    public async Task EmptyQuery_KeepsInputOrder()
    {
        var list = List();
        await Assert.That(string.Join("|", list.Results)).IsEqualTo(string.Join("|", Items));
    }

    [Test]
    public async Task NoMatch_IsEmpty_WithDefaultSelectionAndPreview()
    {
        var list = List(s => "preview:" + s);
        list.SetQuery("zzz");

        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.SelectedIndex).IsEqualTo(0);
        await Assert.That(list.Preview).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Selection_Navigation_Clamps_AndRefreshesPreview()
    {
        var list = List(s => "preview:" + s);
        list.SetQuery("ses");

        await Assert.That(list.Preview).IsEqualTo("preview:" + list.Results[0]);
        list.SelectNext();
        await Assert.That(list.SelectedIndex).IsEqualTo(1);
        await Assert.That(list.Preview).IsEqualTo("preview:" + list.Results[1]);
        list.Move(99);
        await Assert.That(list.SelectedIndex).IsEqualTo(list.Count - 1);
        list.Move(-99);
        await Assert.That(list.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task WriteMatchIndices_ReportsLeftmostSubsequencePositions()
    {
        var list = List();
        list.SetQuery("cs"); // matches "clear session" at positions 0 and 6
        int row = IndexOf(list, "clear session");

        int count, first, second;
        {
            Span<int> indices = stackalloc int[2];
            count = list.WriteMatchIndices(row, indices);
            first = indices[0];
            second = indices[1];
        }

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(6);
    }

    [Test]
    public async Task WriteMatchIndices_EmptyQueryOrSmallBuffer_YieldsNoHighlight()
    {
        var list = List();
        await Assert.That(list.WriteMatchIndices(0, stackalloc int[4])).IsEqualTo(0);

        list.SetQuery("ses");
        await Assert.That(list.WriteMatchIndices(0, stackalloc int[1])).IsEqualTo(0);
    }

    [Test]
    public async Task IncrementalNarrowing_ProducesSameSetAsFullScan()
    {
        var incremental = List();
        incremental.SetQuery("s");
        incremental.SetQuery("se");
        incremental.SetQuery("ses");
        incremental.SetQuery("sess");

        var fresh = List();
        fresh.SetQuery("sess");

        await Assert.That(incremental.Count).IsEqualTo(fresh.Count);
        for (int i = 0; i < fresh.Count; i++)
        {
            await Assert.That(incremental.Results[i]).IsEqualTo(fresh.Results[i]);
        }
    }

    [Test]
    public async Task SetItems_RebuildsBuffer_AndKeepsQuery()
    {
        var list = List();
        list.SetQuery("ses");
        list.SetItems(["session one", "session two", "session three", "session four", "session five", "help"]);

        await Assert.That(list.Count).IsEqualTo(5);
        await Assert.That(list.Query).IsEqualTo("ses");
        await Assert.That(IndexOf(list, "help") < 0).IsTrue();
    }

    [Test]
    public async Task FuzzyMatcher_MatchIndices_IsZeroAlloc()
    {
        const string candidate = "clear session";
        Span<int> warm = stackalloc int[2];
        _ = FuzzyMatcher.MatchIndices("cs", candidate, warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Span<int> indices = stackalloc int[2];
        int count = FuzzyMatcher.MatchIndices("cs", candidate, indices);
        long after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(after - before).IsEqualTo(0);
    }

    [Test]
    public async Task Refilter_OverWarmCache_IsZeroAlloc()
    {
        FuzzyMatcher.ClearCache();
        // Enough rows that per-item allocations would dwarf the tolerance, but
        // few enough (query pairs) to stay under FuzzyMatcher's 4096-entry LRU.
        var items = new string[1000];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = "item " + i.ToString("D4") + " session";
        }

        string[] queries = ["ses", "item", "99"];
        var warm = new FilterList<string>(items, static s => s);
        foreach (string q in queries)
        {
            warm.SetQuery(q);
        }

        var list = new FilterList<string>(items, static s => s);
        // Warm this instance's method paths too, then measure repeated refilters.
        foreach (string q in queries)
        {
            list.SetQuery(q);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string q in queries)
        {
            list.SetQuery(q);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(list.Count).IsGreaterThan(0);
        // A full scan over 1000 cached candidates that rebuilt the result list
        // per keystroke would allocate tens of KB; this bound proves it doesn't.
        await Assert.That(delta).IsLessThan(4096);
    }

    [Test]
    public async Task Paint_HighlightsMatches_AndRendersPreview()
    {
        var list = List(s => "preview line one\npreview line two:" + s);
        list.SetQuery("ses");
        var buffer = TestRender.Buffer(40, 6);

        FilterListView.Paint(list, buffer, new Rect(0, 0, 20, 3), new Rect(22, 0, 16, 3));

        // Selected row marker.
        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo((int)'>');

        // Matched characters carry the underline flag; non-matched ones do not.
        // Read everything out of the Span before an await (ref structs cannot
        // live across an await boundary).
        string text = list.TextOfResult(0);
        int count, matched, plain;
        {
            Span<int> matches = stackalloc int[list.Query.Length];
            count = list.WriteMatchIndices(0, matches);
            matched = matches[0];
            plain = FindPlain(text, matches, count);
        }

        await Assert.That(count).IsGreaterThan(0);
        await Assert.That(buffer.Get(2 + matched, 0).Rune).IsEqualTo((int)text[matched]);
        await Assert.That(buffer.Get(2 + matched, 0).Style.Attrs.HasFlag(StyleAttr.Underline)).IsTrue();
        await Assert.That(buffer.Get(2 + plain, 0).Style.Attrs.HasFlag(StyleAttr.Underline)).IsFalse();

        // Preview pane shows the first line of the selected item's preview.
        string firstPreviewLine = list.Preview.Split('\n')[0];
        await Assert.That(buffer.Get(22, 0).Rune).IsEqualTo((int)firstPreviewLine[0]);
    }

    private static int IndexOf(FilterList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list.Results[i] == value)
            {
                return i;
            }
        }

        throw new InvalidOperationException("item not in results: " + value);
    }

    private static int FindPlain(string text, Span<int> matches, int count)
    {
        for (int i = 0; i < text.Length; i++)
        {
            bool isMatch = false;
            for (int m = 0; m < count; m++)
            {
                if (matches[m] == i)
                {
                    isMatch = true;
                    break;
                }
            }

            if (!isMatch)
            {
                return i;
            }
        }

        throw new InvalidOperationException("no unmatched character");
    }
}
