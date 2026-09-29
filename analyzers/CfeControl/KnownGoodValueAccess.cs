// KnownGoodValueAccess.cs — the NEGATIVE half of the CFE0001 positive control.
//
// A control that only proves "the analyzer fires somewhere" would be satisfied
// by an analyzer that fires on EVERYTHING. This file is the companion: correct,
// guarded code that must NOT be reported. Together the two files distinguish
// "the analyzer works" from "the analyzer is a noise machine", which is the
// whole point of measuring 21 false positives before baselining them.
//
// The guard shape used here is the canonical early-return, which is also the
// shape the analyzer's walker does NOT model (see CfeValueBaselineTests). That
// is deliberate: it is the one shape where a false negative is expected, so if
// this file ever starts being reported, the walker's behaviour changed and the
// baseline reasons in CfeValueBaselineTests must be re-examined.

using CSharpFunctionalExtensions;

namespace Harbor.CfeControl;

/// <summary>Deliberately compliant sample used only as an analyzer probe.</summary>
internal static class KnownGoodValueAccess
{
    /// <summary>
    ///     Unwraps a result only after checking <see cref="Result{T}.IsFailure" />.
    /// </summary>
    /// <param name="input">A value that may fail.</param>
    /// <returns>The unwrapped value, or a fallback when the input failed.</returns>
    internal static string Guarded(Result<string> input)
    {
        if (input.IsFailure)
        {
            return "fallback";
        }

        return input.Value;
    }
}
