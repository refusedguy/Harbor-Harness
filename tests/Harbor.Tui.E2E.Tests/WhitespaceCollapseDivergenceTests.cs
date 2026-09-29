using System.Reflection;
using Harbor.E2E.Framework;
using Harbor.Ui.Framework.Projection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.E2E.Tests;

/// <summary>
///     Issue #574's duplicate census counted a <c>CollapseWhitespace</c> triad
///     that lives entirely under <c>contrib/</c> — projects no CI job compiles —
///     and so it never looked at the one copy that is actually live: the
///     tear-tolerant matcher inside <see cref="TuiDriver" />.
///     <para>
///         The two same-named operations are <b>not</b> the same operation:
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="PanelText.SingleLine" /> — canonical, live, production.
///             It <b>replaces</b> CR and LF with a space so a multi-line log
///             message fits one display row. Every other character survives:
///             tabs, and runs of spaces, are left exactly as they were.
///         </item>
///         <item>
///             the E2E matcher <b>deletes</b> every whitespace character, so a
///             pattern still matches when the renderer tore one logical row
///             across several grid lines. Tabs, space runs and CR/LF all
///             disappear — the result is shorter than the input.
///         </item>
///     </list>
///     <para>
///         Same name, opposite intent, and the divergence is invisible at the
///         call site because both take a string and return a string. This is
///         therefore not a duplication to merge but two separate requirements
///         wearing one name, and the fix is to keep both and tell them apart.
///         These tests pin the split so the next census cannot fold them back
///         together on the strength of the name alone.
///     </para>
/// </summary>
public class WhitespaceCollapseDivergenceTests
{
    private const string StripWhitespace = "StripWhitespace";
    private const string CollapseWhitespace = "CollapseWhitespace";

    private const BindingFlags AllStatic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    /// <summary>
    ///     The production contract: a row-fitting substitute, not a deletion.
    ///     Length is preserved, so a caller can still budget columns by it.
    /// </summary>
    [Test]
    public async Task PanelText_SingleLine_ReplacesLineBreaks_AndKeepsEveryOtherWhitespace()
    {
        // CR and LF each become a space — the row keeps one slot per break.
        await Assert.That(PanelText.SingleLine("alpha\r\nbeta")).IsEqualTo("alpha  beta");

        // A tab is not a line break, and neither is a run of spaces.
        await Assert.That(PanelText.SingleLine("alpha\tbeta")).IsEqualTo("alpha\tbeta");
        await Assert.That(PanelText.SingleLine("alpha   beta")).IsEqualTo("alpha   beta");

        // No allocation on the empty path, and no null dereference.
        await Assert.That(PanelText.SingleLine(string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(PanelText.SingleLine(null!)).IsEqualTo(string.Empty);
    }

    /// <summary>
    ///     The E2E contract: deletion, so that a pattern matches across a tear.
    ///     This is what makes the two helpers impossible to share.
    /// </summary>
    [Test]
    public async Task TearTolerantMatcher_StripsEveryWhitespaceCharacter_InsteadOfReplacingIt()
    {
        var method = typeof(TuiDriver).GetMethod(StripWhitespace, AllStatic);
        await Assert.That(method).IsNotNull();

        var stripped = (string)method!.Invoke(null, ["alpha\r\n  beta\tgamma"])!;

        // Every whitespace character is gone — this is the opposite of SingleLine,
        // which would have returned "alpha    beta\tgamma".
        await Assert.That(stripped).IsEqualTo("alphabetagamma");
    }

    /// <summary>
    ///     The name itself is the defect: <c>CollapseWhitespace</c> already means
    ///     <see cref="PanelText.SingleLine" />, so the E2E helper had to give it
    ///     up. Asserted in both directions — a name that is present fails, a
    ///     name that is missing fails — so neither a half-applied rename nor a
    ///     rename that never happened can pass.
    /// </summary>
    [Test]
    public async Task TuiDriver_NamesTheTearTolerantStrip_StripWhitespace_NotCollapseWhitespace()
    {
        // Non-vacuity: prove this probe can actually see TuiDriver's methods at
        // all, so the "IsNull" assertion below cannot pass by looking at nothing.
        await Assert.That(typeof(TuiDriver).GetMethods(AllStatic).Length).IsGreaterThan(0);

        await Assert.That(typeof(TuiDriver).GetMethod(StripWhitespace, AllStatic)).IsNotNull();
        await Assert.That(typeof(TuiDriver).GetMethod(CollapseWhitespace, AllStatic)).IsNull();
    }

    /// <summary>
    ///     The payoff: the two helpers, fed the same input, do not agree — and
    ///     must not be made to. This is the assertion a future "these look the
    ///     same, let me merge them" would have to break.
    /// </summary>
    [Test]
    public async Task TearTolerantStrip_AndPanelTextSingleLine_DisagreeOnTheSameInput()
    {
        const string Torn = "alpha\r\n\tbeta   gamma";
        var method = typeof(TuiDriver).GetMethod(StripWhitespace, AllStatic);
        await Assert.That(method).IsNotNull();

        var stripped = (string)method!.Invoke(null, [Torn])!;
        var singleLine = PanelText.SingleLine(Torn);

        await Assert.That(stripped).IsNotEqualTo(singleLine);
        await Assert.That(stripped.Length).IsLessThan(singleLine.Length);
    }
}
