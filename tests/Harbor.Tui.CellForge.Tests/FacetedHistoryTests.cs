using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2706 P13: the domain-free faceted history state. Covers the primitive's
/// contract: text and facet filtering, AND across keys with OR within a key,
/// facet counts that respect other constraints, a cursor that tracks the
/// filtered result, deterministic rows and paint placement.
/// </summary>
public class FacetedHistoryTests
{
    private static HistoryEntry Entry(string id, string text, string detail = "", params HistoryFacet[] facets) =>
        new(id, text, facets, detail);

    private static FacetedHistoryState WithEntries(params HistoryEntry[] entries)
    {
        var state = new FacetedHistoryState();
        state.Replace(entries);
        return state;
    }

    [Test]
    public async Task EmptyState_HasNoVisibleEntryOrSelection()
    {
        var state = new FacetedHistoryState();

        await Assert.That(state.Entries).IsEmpty();
        await Assert.That(state.Visible()).IsEmpty();
        await Assert.That(state.TotalCount).IsEqualTo(0);
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.Selected).IsNull();
        await Assert.That(state.FacetValues("exit")).IsEmpty();

        IReadOnlyList<HistoryRow> rows = state.Rows(20, 5);
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[0].Text).IsEqualTo("History (0/0)");
        await Assert.That(rows[1].Text).IsEqualTo("facets: all");
        await Assert.That(rows[2].Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Replace_ClampsCursorIntoNewResultSize()
    {
        var state = WithEntries(
            Entry("1", "one"),
            Entry("2", "two"),
            Entry("3", "three"));

        await Assert.That(state.MoveCursor(10)).IsTrue();
        await Assert.That(state.Cursor).IsEqualTo(2);
        await Assert.That(state.Selected?.Id).IsEqualTo("3");

        // Shrinking the collection pulls the cursor back; a clamp onto the
        // current row is a no-op.
        await Assert.That(state.Replace([Entry("1", "one")])).IsTrue();
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.MoveCursor(5)).IsFalse();
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.Selected?.Id).IsEqualTo("1");
    }

    [Test]
    public async Task Query_FiltersTextAndDetail_CaseInsensitive()
    {
        var state = WithEntries(
            Entry("1", "alpha", "first"),
            Entry("2", "beta", "ALPHA detail"),
            Entry("3", "gamma", "third"));

        await Assert.That(state.SetQuery("ALPHA")).IsTrue();
        await Assert.That(state.Visible().Count).IsEqualTo(2);
        await Assert.That(state.Visible()[0].Id).IsEqualTo("1");
        await Assert.That(state.Visible()[1].Id).IsEqualTo("2");

        // The same string is a no-op; a changed query resets the cursor.
        await Assert.That(state.SetQuery("ALPHA")).IsFalse();
        state.MoveCursor(1);
        await Assert.That(state.SetQuery("gamma")).IsTrue();
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.Visible().Count).IsEqualTo(1);
        await Assert.That(state.Selected?.Id).IsEqualTo("3");

        await Assert.That(state.SetQuery("missing")).IsTrue();
        await Assert.That(state.Visible()).IsEmpty();
        await Assert.That(state.Selected).IsNull();
        await Assert.That(state.SetQuery(null)).IsTrue();
        await Assert.That(state.Visible().Count).IsEqualTo(3);
    }

    [Test]
    public async Task Facets_AndAcrossKeys_OrWithinOneKey()
    {
        var state = WithEntries(
            Entry("1", "one", "", new HistoryFacet("exit", "ok"), new HistoryFacet("host", "box")),
            Entry("2", "two", "", new HistoryFacet("exit", "err"), new HistoryFacet("host", "box")),
            Entry("3", "three", "", new HistoryFacet("exit", "ok"), new HistoryFacet("host", "other")));

        await Assert.That(state.ToggleFacet("exit", "ok")).IsTrue();
        await Assert.That(state.Visible().Count).IsEqualTo(2);

        await Assert.That(state.ToggleFacet("host", "box")).IsTrue();
        await Assert.That(state.Visible().Count).IsEqualTo(1);
        await Assert.That(state.Selected?.Id).IsEqualTo("1");

        // A second value for "exit" widens that key instead of requiring both.
        await Assert.That(state.ToggleFacet("exit", "err")).IsTrue();
        await Assert.That(state.Visible().Count).IsEqualTo(2);

        await Assert.That(state.ToggleFacet("host", "box")).IsTrue();
        await Assert.That(state.Visible().Count).IsEqualTo(3);
        await Assert.That(state.ClearFacets()).IsTrue();
        await Assert.That(state.Facets).IsEmpty();
        await Assert.That(state.ClearFacets()).IsFalse();
    }

    [Test]
    public async Task FacetValues_RespectQueryAndOtherKeys_AndSortDeterministically()
    {
        var state = WithEntries(
            Entry("1", "one", "", new HistoryFacet("exit", "ok"), new HistoryFacet("host", "box")),
            Entry("2", "two", "", new HistoryFacet("exit", "err"), new HistoryFacet("host", "box")),
            Entry("3", "three", "", new HistoryFacet("exit", "ok"), new HistoryFacet("host", "other")));

        IReadOnlyList<FacetCount> exits = state.FacetValues("exit");
        await Assert.That(exits.Count).IsEqualTo(2);
        await Assert.That(exits[0].Value).IsEqualTo("ok");
        await Assert.That(exits[0].Count).IsEqualTo(2);
        await Assert.That(exits[1].Value).IsEqualTo("err");
        await Assert.That(exits[1].Count).IsEqualTo(1);

        state.ToggleFacet("host", "box");
        IReadOnlyList<FacetCount> narrowed = state.FacetValues("exit");
        await Assert.That(narrowed.Count).IsEqualTo(2);
        await Assert.That(narrowed[0].Value).IsEqualTo("err");
        await Assert.That(narrowed[1].Value).IsEqualTo("ok");

        // Selecting a value for the queried key must not erase its own counts.
        state.ToggleFacet("exit", "ok");
        await Assert.That(state.FacetValues("exit")[0].Count).IsEqualTo(1);

        IReadOnlyList<FacetCount> hosts = state.FacetValues("host");
        await Assert.That(hosts.Count).IsEqualTo(2);
        await Assert.That(hosts[0].Value).IsEqualTo("box");
        await Assert.That(hosts[1].Value).IsEqualTo("other");

        state.SetQuery("three");
        IReadOnlyList<FacetCount> byQuery = state.FacetValues("host");
        await Assert.That(byQuery.Count).IsEqualTo(1);
        await Assert.That(byQuery[0].Value).IsEqualTo("other");
        await Assert.That(byQuery[0].Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cursor_TracksFilteredResult()
    {
        var state = WithEntries(
            Entry("1", "one"),
            Entry("2", "two"),
            Entry("3", "three"),
            Entry("4", "four"));

        await Assert.That(state.SetCursor(3)).IsTrue();
        await Assert.That(state.Selected?.Id).IsEqualTo("4");
        await Assert.That(state.MoveCursor(1)).IsFalse();
        await Assert.That(state.MoveCursor(-10)).IsTrue();
        await Assert.That(state.Selected?.Id).IsEqualTo("1");

        state.SetQuery("four");
        await Assert.That(state.Cursor).IsEqualTo(0);
        await Assert.That(state.Selected?.Id).IsEqualTo("4");
        await Assert.That(state.MoveCursor(5)).IsFalse();
        await Assert.That(state.Selected?.Id).IsEqualTo("4");
    }

    [Test]
    public async Task Rows_AreDeterministicAndTruncateToCellWidth()
    {
        var state = WithEntries(
            Entry("1", "alpha", "first"),
            Entry("2", "beta", "second"));

        IReadOnlyList<HistoryRow> first = state.Rows(20, 6);
        IReadOnlyList<HistoryRow> second = state.Rows(20, 6);
        await Assert.That(first.Count).IsEqualTo(5);
        for (int i = 0; i < first.Count; i++)
        {
            await Assert.That(second[i].Text).IsEqualTo(first[i].Text);
            await Assert.That(second[i].IsCursor).IsEqualTo(first[i].IsCursor);
        }

        await Assert.That(first[0].Text).IsEqualTo("History (2/2)");
        await Assert.That(first[2].Text).IsEqualTo("> alpha");
        await Assert.That(first[2].IsCursor).IsTrue();
        await Assert.That(first[3].Text).IsEqualTo("  beta");
        await Assert.That(first[4].Text).IsEqualTo("first");

        state.ToggleFacet("exit", "ok");
        await Assert.That(state.Rows(20, 6)[1].Text).IsEqualTo("facets: exit=ok");

        state.ClearFacets();
        IReadOnlyList<HistoryRow> narrow = state.Rows(6, 6);
        await Assert.That(narrow[2].Text).IsEqualTo("> alp…");

        var wide = WithEntries(Entry("w", "漢漢漢漢", "preview"));
        await Assert.That(wide.Rows(5, 4)[2].Text).IsEqualTo("> 漢…");
    }

    [Test]
    public async Task Paint_PlacesRowsAndReversesCursor()
    {
        var state = WithEntries(
            Entry("1", "alpha", "first"),
            Entry("2", "beta", "second"));

        // The test project pins unqualified rendering names to the shared UI
        // namespace, so engine types are qualified through EngineCells.
        var buffer = new EngineCells.ScreenBuffer(20, 6);
        state.Paint(
            buffer,
            new EngineCells.Rect(0, 0, 20, 6),
            new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Bold));

        await Assert.That(buffer.Get(0, 0).Rune).IsEqualTo('H');
        await Assert.That(buffer.Get(0, 0).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Bold);
        await Assert.That(buffer.Get(0, 1).Rune).IsEqualTo('f');
        await Assert.That(buffer.Get(0, 2).Rune).IsEqualTo('>');
        await Assert.That(buffer.Get(0, 2).Flags).IsEqualTo((ushort)EngineCells.StyleAttr.Reverse);
        await Assert.That(buffer.Get(2, 2).Rune).IsEqualTo('a');

        // A clip too small for a row is a no-op, not a half-painted frame.
        var narrow = new EngineCells.ScreenBuffer(1, 4);
        state.Paint(narrow, new EngineCells.Rect(0, 0, 1, 4));
        await Assert.That(narrow.Get(0, 0).Rune).IsEqualTo(' ');
    }
}
