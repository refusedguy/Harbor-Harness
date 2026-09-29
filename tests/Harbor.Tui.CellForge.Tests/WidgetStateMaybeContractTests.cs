using CSharpFunctionalExtensions;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     The <c>Maybe&lt;T&gt;</c> widget-state contract (#592): a widget that has real
///     states says so in its public signature, and "is it showing" is answered once.
/// </summary>
/// <remarks>
///     <para>
///         <c>ToolCallBlock.Body</c> was <c>ToolResultBody?</c> and the paint/measure path
///         dereferenced it through four <c>_body!</c> suppressions. Each suppression existed
///         because the "no body yet" guard lived in a <i>different</i> method than the deref:
///         <c>Paint</c> returned early on absence, and <c>PaintOutputBody</c>,
///         <c>BodyLineCount</c>, <c>ExpandedBodyLineCount</c> and <c>DiffLineCount</c> — none of
///         which can see that guard — suppressed instead. No live NRE, but a fourth entry
///         point would have inherited one that nothing in the type could catch.
///     </para>
///     <para>
///         <c>WhichKeyHelpOverlay</c> had the same class of problem without the suppression:
///         <c>Visible</c> and <c>Context</c> were independent state that could disagree, and
///         the layer re-derived a third answer from the conjunction of the two. These tests
///         pin the collapsed shape.
///     </para>
/// </remarks>
public class WidgetStateMaybeContractTests
{
    private static ToolCallBlock RunningBlock() => new(new ToolCallInfo("t1", "edit", "src/a.cs"));

    // ── ToolCallBlock: absence is a state, not a null ─────────────────────────

    [Test]
    public async Task ToolCallBlock_Running_ReportsAbsenceNotNull()
    {
        ToolCallBlock block = RunningBlock();

        await Assert.That(block.Status).IsEqualTo(ToolCallStatus.Running);
        await Assert.That(block.Body.HasNoValue).IsTrue()
            .Because(
                "A running tool call has produced no result yet. That is a state, so the " +
                "signature says Maybe<T>.None and every consumer has to decide what a running " +
                "card looks like, instead of inheriting a null it cannot explain (#592).");
    }

    [Test]
    public async Task ToolCallBlock_Complete_ReportsPresence()
    {
        ToolCallBlock block = RunningBlock();
        block.Complete(new ToolResultBody("done", false, TimeSpan.FromMilliseconds(5)));

        await Assert.That(block.Body.HasValue).IsTrue();
        await Assert.That(block.Body.Value.Output).IsEqualTo("done");
    }

    [Test]
    public async Task ToolCallBlock_Running_MeasureAndEstimateAgreeOnOneLine()
    {
        // The invariant the four suppressions were papering over: a body-less card is a
        // pure one-liner, and Measure, CheapEstimate and Paint all have to agree on that.
        // They each re-derived the `_body is not null` guard independently, which is how
        // the three copies could drift without anything failing.
        ToolCallBlock block = RunningBlock();

        var measure = block.Measure(80);
        int cheap = block.CheapEstimate(80);

        await Assert.That(measure.MinLines).IsEqualTo(1);
        await Assert.That(cheap).IsEqualTo(1);
        await Assert.That(measure.MinLines).IsEqualTo(cheap)
            .Because(
                "A disagreement between the exact and the cheap measurement does not throw — it " +
                "misplaces every block below it in the timeline, which is the damage #551 reports " +
                "as black bands. With a body-less card there is nothing to measure, so both are 1.");
    }

    [Test]
    public async Task ToolCallBlock_Running_PaintsHeaderOnly_AndDoesNotThrow()
    {
        ToolCallBlock block = RunningBlock();
        var buffer = new ScreenBuffer(80, 4);

        // Before the Maybe conversion this path was reachable only because Paint held the
        // guard. Calling the line-count helpers directly is what used to be an NRE.
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 4), 0));

        string art = GridDump.Art(buffer);
        await Assert.That(art).Contains("edit");
        await Assert.That(art).DoesNotContain("done");
    }

    [Test]
    public async Task ToolCallBlock_Complete_PaintsBody()
    {
        ToolCallBlock block = RunningBlock();
        block.Complete(new ToolResultBody("the body text", false, TimeSpan.FromMilliseconds(5)));
        var buffer = new ScreenBuffer(80, 4);

        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 80, 4), 0));

        await Assert.That(GridDump.Art(buffer)).Contains("the body text")
            .Because("Presence must still paint exactly as before — the Maybe is a signature change, not a rendering change.");
    }

    // ── WhichKeyHelpOverlay: one authority for "is it showing" ───────────────

    [Test]
    public async Task WhichKey_ShownState_HasASingleAuthority()
    {
        var overlay = new WhichKeyHelpOverlay();
        await Assert.That(overlay.IsShown).IsFalse();

        overlay.Show();
        await Assert.That(overlay.IsShown).IsTrue();
        await Assert.That(overlay.Visible).IsEqualTo(overlay.IsShown)
            .Because("Visible is an alias of IsShown, not a second flag that can drift from it.");

        overlay.Hide();
        await Assert.That(overlay.IsShown).IsFalse();
    }

    [Test]
    public async Task WhichKey_NoContext_ReportsAbsence()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show();

        await Assert.That(overlay.Context.HasNoValue).IsTrue();
        await Assert.That(overlay.HasContext).IsFalse()
            .Because(
                "HasContext used to re-derive the lookup from the backing field with two independent " +
                "`_context?.` probes, so it and the renderer could answer differently. Both now read " +
                "through the one Context value (#592).");
    }

    [Test]
    public async Task WhichKey_WithContext_ReportsPresence()
    {
        var overlay = new WhichKeyHelpOverlay();
        overlay.Show(new WhichKeyContext("timeline", "palette"));

        await Assert.That(overlay.Context.HasValue).IsTrue();
        await Assert.That(overlay.Context.Value.FocusedPanelId).IsEqualTo("timeline");
        await Assert.That(overlay.HasContext).IsTrue();
    }

    [Test]
    public async Task WhichKey_EmptyContextStrings_AreNotAContextSection()
    {
        var overlay = new WhichKeyHelpOverlay();

        // A context that names nothing is present but has nothing to print. The two
        // questions are distinct and both are now derived from one value.
        overlay.Show(new WhichKeyContext(null, null));
        await Assert.That(overlay.Context.HasValue).IsTrue();
        await Assert.That(overlay.HasContext).IsFalse();

        overlay.Show(new WhichKeyContext(string.Empty, "palette"));
        await Assert.That(overlay.HasContext).IsTrue();
    }

    [Test]
    public async Task WhichKey_Hide_DoesNotClearTheContext()
    {
        // Behaviour preservation: Hide() clears the shown flag and nothing else, so a host
        // can prime the context with SetContext while hidden and pass it to the Show(context)
        // that follows. Clearing it in Hide() would have been a silent behaviour change.
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetContext(new WhichKeyContext("timeline", null));
        overlay.Hide();

        await Assert.That(overlay.IsShown).IsFalse();
        await Assert.That(overlay.Context.HasValue).IsTrue()
            .Because("The shown flag is the state; the context is content the host supplied.");
    }

    [Test]
    public async Task WhichKey_ShowWithoutContext_ReplacesAPrimedContext()
    {
        // Pre-existing behaviour, unchanged by #592, and the reason the test above stops at
        // Hide(): Show() assigns unconditionally, so its default null argument overwrites a
        // primed context with None. That was true before the conversion too — the old
        // `public void Show(WhichKeyContext? context = null) { _context = context; }` did
        // exactly the same assignment. Pinned here so that "Hide() keeps the context" is not
        // misread as "the context survives Show()": it survives to be passed, not to persist.
        var overlay = new WhichKeyHelpOverlay();
        overlay.SetContext(new WhichKeyContext("timeline", null));
        overlay.Hide();

        overlay.Show();

        await Assert.That(overlay.IsShown).IsTrue();
        await Assert.That(overlay.Context.HasNoValue).IsTrue()
            .Because("Show() with no argument means 'no context', not 'keep the one I had'.");
    }

    [Test]
    public async Task WhichKeyLayer_NamesGeometrySeparatelyFromState()
    {
        // The layer used to answer "is this showing" as (shown AND big enough), which is two
        // questions under one name: a shown overlay reported Visible == false on a small
        // terminal and the host could not tell a too-small box from a hidden one.
        var overlay = new WhichKeyHelpOverlay();
        var layer = new WhichKeyHelpOverlayLayer(overlay);

        layer.Sync(new Rect(0, 0, 80, 24));
        await Assert.That(overlay.IsShown).IsFalse();
        await Assert.That(layer.HasRoom).IsTrue()
            .Because("A hidden overlay in a large viewport has room — the two are independent, which is the point.");
        await Assert.That(layer.Visible).IsFalse();

        overlay.Show();
        await Assert.That(layer.Visible).IsTrue();

        // Same shown state, too small a box: Visible flips, IsShown does not.
        layer.Sync(new Rect(0, 0, 10, 4));
        await Assert.That(overlay.IsShown).IsTrue();
        await Assert.That(layer.HasRoom).IsFalse();
        await Assert.That(layer.Visible).IsFalse()
            .Because(
                "Preserved behaviour: the conjunction is unchanged. What changed is that the host " +
                "can now read IsShown and HasRoom separately and see which one failed.");
    }
}
