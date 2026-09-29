// KnownBadValueAccess.cs — the CFE0001 POSITIVE CONTROL.
//
// This file is the answer to "prove the analyzer is not vacuous". It is
// compiled on demand by CfeControlPositiveControlTests, which asserts that the
// build emits CFE0001 for this file. If the analyzer ever silently stops
// resolving — a broken version pin, a NoWarn that eats everything, a package
// that resolves to nothing — this file compiles clean, the diagnostic goes
// missing, and the test fails.
//
// It is never compiled by the normal solution build; the project is not in
// Harbor.slnx (see Harbor.CfeControl.csproj).
//
// SHAPE MATTERS: the access below is a `Result<T>.Value` read inside a plain
// method body with no IsSuccess/IsFailure check anywhere in that body, which is
// the exact shape CFE0001 is specified to catch. A snippet the analyzer is
// merely *permitted* to skip (a `?.Value`, a `.Value` outside a method body)
// would prove nothing — a guard that only fires on some inputs is exactly the
// failure mode this file exists to exclude.

using CSharpFunctionalExtensions;

namespace Harbor.CfeControl;

/// <summary>Deliberately non-compliant sample used only as an analyzer probe.</summary>
internal static class KnownBadValueAccess
{
    /// <summary>
    ///     Returns the value of a result that may be a failure. The returned
    ///     <c>Result&lt;T&gt;</c> is checked by neither this method nor its
    ///     call site within this file, so reading <c>.Value</c> can throw
    ///     <c>ResultFailureException</c>.
    /// </summary>
    /// <param name="input">A value that may fail.</param>
    /// <returns>The unwrapped value.</returns>
    internal static string Unguarded(Result<string> input) => input.Value;
}
