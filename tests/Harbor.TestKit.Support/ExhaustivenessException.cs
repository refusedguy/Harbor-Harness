// ExhaustivenessException.cs — the failure the totality check raises.
//
// WHY A TYPE OF ITS OWN
// ----------------------
// The check fails a TEST, and a test failure has to say what is wrong. A
// bare InvalidOperationException would say "the table is short" without
// saying WHICH member is missing, and the caller of the check is a guard
// whose whole job is to name the offender.
//
// The message is the report. It names the table, the enum, every missing
// member, and the caller's own reason for the rule — one run, every
// offender, the way the guards in this repository report (see the
// "Assert.Multiple" work in #1859).
//
// It is NOT an assertion exception on purpose: the check lives in a helper
// library that carries no reference to the test framework, and a type of its
// own is what lets a caller (and a control) name it.

namespace Harbor.TestKit;

/// <summary>
///     Thrown when a table does not cover every member of the enum it maps.
/// </summary>
public sealed class ExhaustivenessException : Exception
{
    /// <summary>
    ///     Creates the failure.
    /// </summary>
    /// <param name="message">
    ///     The report: which table, which enum, which members are missing and
    ///     why the rule exists.
    /// </param>
    public ExhaustivenessException(string message)
        : base(message)
    {
    }
}
