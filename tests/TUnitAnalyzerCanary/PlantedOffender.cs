using TUnit.Assertions;

namespace TUnitAnalyzerCanary;

/// <summary>
///     The planted offences of the TUnit analyzer gate (#1836). This file is
///     never RUN — it is COMPILED by harbor-gates check-tunit-analyzer-gate, and
///     the tool asserts what the compiler says about the two methods below.
/// </summary>
/// <remarks>
///     <para>
///         The two methods are the two halves of the control:
///     </para>
///     <para>
///         <b>Planted_Offender_MustBeReported</b> carries an
///         <c>Assert.That(true)</c>, which TUnit's TUnitAssertions0005 reports
///         ("Assert.That(...) should not be used with a constant value") and
///         which tests/Directory.Build.props promotes to an error. If the
///         analyzer stopped shipping, or the promotion stopped applying, this
///         method would compile clean and the tool would fail: the gate
///         cannot be "green because nothing is checked".
///     </para>
///     <para>
///         <b>Waived_Offender_MustNotBeReported</b> carries the same offence
///         with a scoped suppression around it. A waiver that grows past its
///         own site would swallow this one too, and the tool would notice
///         that the expected single diagnostic is not the one reported. The
///         waiver is here rather than in the tool's fixture for the same
///         reason the other 29 waivers in the tree are at their call sites:
///         the reason belongs next to the code it excuses.
///     </para>
/// </remarks>
public class PlantedOffender
{
    /// <summary>
    ///     The planted offender. The build of this project is EXPECTED to
    ///     fail, and the tool asserts that it fails for exactly this reason
    ///     and at exactly this line.
    /// </summary>
    [Test]
    public async Task Planted_Offender_MustBeReported()
    {
        await Assert.That(true).IsTrue();
    }

    /// <summary>
    ///     The planted waiver. The build of this project must NOT report
    ///     anything here: the suppression covers this site and nothing else.
    /// </summary>
    [Test]
    public async Task Waived_Offender_MustNotBeReported()
    {
#pragma warning disable TUnitAssertions0005 // planted waiver: the gate's tool asserts it suppresses exactly this site and nothing else
        await Assert.That(false).IsTrue();
#pragma warning restore TUnitAssertions0005
    }
}
