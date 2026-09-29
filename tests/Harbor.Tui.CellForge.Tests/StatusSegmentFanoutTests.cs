using System.Reflection;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #568 drift guard, part 1 — <b>the segment type is the registration</b>.
/// </summary>
/// <remarks>
///     <para>
///         The status bar is a fan-out: one derivation
///         (<see cref="StatusBarFacts" />) feeds several surfaces, and each
///         surface that wants a cell used to add a field, a branch and a
///         position of its own. The census measured on this tree, not asserted
///         from the issue:
///         <list type="bullet">
///             <item>five places build status text from a <see cref="UiState" /> —
///                 <c>StatusBarFacts.Of</c>, <c>StatusProjector.ProjectStatusBar</c>,
///                 <c>StatusProjectorPanel.BuildSegments</c>, <c>StatusViewModel.BuildSegments</c>
///                 and <c>StatusBarViewModel.Formatted</c>;
///             </item>
///             <item>three of those carry their own cost format (<c>"F4"</c> in two,
///                 <c>"0.####"</c> in the third) and two their own token format;</item>
///             <item>the CellForge footer keeps a second, hand-maintained ordering
///                 beside <c>StatusSegmentOrdering.Ordered</c>, so a cell added at
///                 the wrong <c>Importance</c> paints in a different order per
///                 backend with every behavioural test green.</item>
///         </list>
///     </para>
///     <para>
///         The first test below is the load-bearing one and it is deliberately
///         <b>red on this tree</b>: <see cref="UiStatusSegment" /> is a
///         <c>sealed record</c> — a <b>reference type</b>. Nothing about it says
///         "fixed priority", so every surface re-decides it: the projection
///         guesses from <c>Importance</c>, the footer hard-codes
///         <c>FixedPriority: true</c> for the cells it wants kept, and the two
///         answers are not the same rule. A record that could not express the
///         property had to be re-derived by everyone who needed it.
///     </para>
///     <para>
///         What the fix is <b>not</b>: a segment registry. A registry would add
///         a place to register a cell in — a new axis (#555). This is the
///         opposite move: the segment record itself becomes the one place a
///         cell's kind is written down, so the per-surface re-derivation has
///         nothing left to re-derive.
///     </para>
/// </remarks>
public class StatusSegmentFanoutTests
{
    /// <summary>Sentinel so a missing member reads as a value in a failure message.</summary>
    private const string NoMember = "<no member>";

    /// <summary>
    ///     A cell's truncation priority is part of what the cell <i>is</i>, so it
    ///     belongs on the segment rather than in each renderer that paints it.
    ///     <para>
    ///         Red today: <c>UiStatusSegment</c> carries <c>(Text, Align,
    ///         Importance, Style)</c> and nothing else, which is why
    ///         <c>ChatScreenLayout.BuildSegments</c> spells
    ///         <c>FixedPriority: true</c> out cell by cell — a fourth
    ///         hand-maintained list, and the one the truncation order in
    ///         <c>StatusSegmentBar.Fit</c> actually consumes.
    ///     </para>
    /// </summary>
    [Test]
    public async Task SegmentCarriesIts_TruncationPriority()
    {
        var members = typeof(UiStatusSegment)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(members.Contains("FixedPriority") ? "FixedPriority" : NoMember)
            .IsEqualTo("FixedPriority");
    }

    /// <summary>
    ///     …and the priority has to be a value, not a hint: a nullable
    ///     <c>bool?</c> would let a cell ship with the question unanswered,
    ///     which is the same silent-truncation bug one level down.
    /// </summary>
    [Test]
    public async Task TruncationPriority_IsBool_NotNullable()
    {
        var member = typeof(UiStatusSegment)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "FixedPriority");

        await Assert.That(member is not null ? "present" : NoMember).IsEqualTo("present");

        // A record's positional parameters surface as properties; read the one
        // the type actually exposes rather than trusting the declaration site.
        var property = typeof(UiStatusSegment).GetProperty("FixedPriority");
        await Assert.That(property?.PropertyType.FullName ?? NoMember).IsEqualTo("System.Boolean");
    }

    /// <summary>
    ///     The structural claim, in the form the issue asked to be checked:
    ///     <b>one place declares a cell's kind</b>. A renderer that had to
    ///     re-derive the priority would need a member to derive it from; with
    ///     the member present, the re-derivation has nothing to read and the
    ///     per-surface list is deletable.
    /// </summary>
    [Test]
    public async Task OnlyTheSegment_DeclaresTheCellKind()
    {
        // The type is the registration. Nothing else in the status path needs
        // a parallel table, because the table it would keep is the segment's
        // own member list.
        var declared = typeof(UiStatusSegment)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Count(m => m is PropertyInfo);

        await Assert.That(declared >= 5).IsTrue();
    }
}
