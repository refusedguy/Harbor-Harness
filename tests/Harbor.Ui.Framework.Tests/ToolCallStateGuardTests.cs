using System.Globalization;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Converters;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// The gate that makes the next <see cref="ToolCallState"/> a test failure
/// instead of a spinner that never stops (#567).
/// </summary>
/// <remarks>
/// <para>
///     C# cannot express "exhaustive over the named members" for a switch over
///     an enum — the compiler also requires the unnamed domain, and CS8524 is
///     an unconditional error, so a discard-less switch expression does not
///     compile. A compile-time gate is therefore unavailable here, and the
///     <c>_ =&gt;</c> arm every switch is forced to carry is exactly the drift
///     hazard #567 is about.
/// </para>
/// <para>
///     This class supplies the other half. It walks
///     <c>Enum.GetValues&lt;ToolCallState&gt;()</c> — so a member added without
///     a presentation is caught the moment CI runs — and asserts each one is
///     resolvable end to end, and that no presentation silently borrows the
///     running one. Two more tests pin the unnamed domain to a throw, so a
///     corrupt value is loud instead of rendering as a live call.
/// </para>
/// </remarks>
public class ToolCallStateGuardTests
{
    private static ToolCallState[] Declared() => Enum.GetValues<ToolCallState>();

    [Test]
    public async Task Every_Declared_State_Has_A_Pill_And_A_Brush()
    {
        foreach (ToolCallState state in Declared())
        {
            string pill = StatusMappers.ToolCallStateToPill(state);
            string brush = StatusMappers.ToolCallStateToBrushKey(state);

            await Assert.That(pill).IsNotEmpty().Because($"{state} has no pill label");
            await Assert.That(pill).IsNotEqualTo("?").Because($"{state} hit the old unknown-state placeholder");
            await Assert.That(brush).IsNotEmpty().Because($"{state} has no brush key");
        }
    }

    /// <summary>
    /// Every terminal state must be distinguishable from every live one. This is
    /// the invariant the old wildcard arms violated: a stopped call rendered as
    /// running, so it looked like it was still working.
    /// </summary>
    [Test]
    public async Task Terminal_States_Never_Borrow_The_Live_Presentation()
    {
        ToolCallState[] live = [ToolCallState.Pending, ToolCallState.Running];

        foreach (ToolCallState state in Declared().Where(s => s.IsTerminal()))
        {
            foreach (ToolCallState other in live)
            {
                await Assert.That(StatusMappers.ToolCallStateToPill(state))
                    .IsNotEqualTo(StatusMappers.ToolCallStateToPill(other))
                    .Because($"{state} must not read as {other}");
                await Assert.That(StatusMappers.ToolCallStateToBrushKey(state))
                    .IsNotEqualTo(StatusMappers.ToolCallStateToBrushKey(other))
                    .Because($"{state} must not wear {other}'s brush");
            }
        }
    }

    [Test]
    public async Task Every_Declared_State_Has_A_Terminal_Classification()
    {
        foreach (ToolCallState state in Declared())
        {
            // Reading the result is the assertion: an unclassified member cannot
            // exist, because IsTerminal names every member or throws.
            bool terminal = state.IsTerminal();
            await Assert.That(terminal).IsEqualTo(state is ToolCallState.Success or ToolCallState.Error
                or ToolCallState.Cancelled or ToolCallState.TimedOut)
                .Because($"{state} terminal classification drifted");
        }
    }

    /// <summary>
    /// The unnamed domain is a bug, not a state. Every switch throws there
    /// instead of picking a plausible default — this pins that contract so a
    /// future <c>_ =&gt; something-reasonable</c> shows up as a failing test.
    /// </summary>
    [Test]
    public async Task Undeclared_Value_Throws_Rather_Than_Defaulting()
    {
        const ToolCallState bogus = (ToolCallState)99;

        await Assert.That(() => StatusMappers.ToolCallStateToPill(bogus))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => StatusMappers.ToolCallStateToBrushKey(bogus))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => bogus.IsTerminal())
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Member_Set_Is_Exactly_These_Six()
    {
        // Pins the union the collapse chose. A seventh member is a deliberate
        // vocabulary decision, not an accident — this test makes it one.
        await Assert.That(string.Join(",", Declared().Select(s => s.ToString())))
            .IsEqualTo("Pending,Running,Success,Error,Cancelled,TimedOut");
    }

    [Test]
    public async Task Undeclared_Exception_Names_The_Offending_Member()
    {
        // A bare `new ArgumentOutOfRangeException()` loses which value broke it;
        // the shared factory is the one place that message is built.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => StatusMappers.ToolCallStateToPill((ToolCallState)99));

        string message = ex.Message;
        await Assert.That(message).Contains("ToolCallState");
        await Assert.That(message).Contains("99");
        await Assert.That(message).Contains("#567")
            .Because("the message must point at the rule it enforces, not just the value");
        await Assert.That(string.Format(CultureInfo.InvariantCulture, "{0}", ex.ActualValue))
            .IsEqualTo("99")
            .Because("the offending value must survive into the exception, not just the message");
    }
}
