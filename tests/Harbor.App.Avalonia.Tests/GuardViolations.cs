namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #1859: the reporting step the "collect the violators, then assert"
///     guards share.
/// </summary>
/// <remarks>
///     <para>
///         A guard that collects its violators into a list and then asserts the
///         list is empty loses the violators. The
///         <c>Assert.That(violations.Count).IsEqualTo(0)</c> form names none of
///         them: the report says "3" and not which three. The
///         <c>Assert.That(violations).IsEmpty()</c> form shows at most ten in
///         TUnit's collection preview and summarises the rest as "and N more".
///         Either way one CI run names one offender, and the developer fixes
///         one offender per round.
///     </para>
///     <para>
///         <c>Assert.Multiple()</c> collects the failures instead of throwing
///         on the first one, so a single run names every offender. Each
///         violator's own text rides along as the failure's reason: without it
///         the report would say that something is wrong and not what.
///     </para>
/// </remarks>
internal static class GuardViolations
{
    /// <summary>
    ///     Reports every violator in one batch. Returns normally when there is
    ///     nothing to report; throws a single <c>AssertionException</c> naming
    ///     all of them when there is.
    /// </summary>
    /// <param name="violations">The violators to report.</param>
    /// <param name="reason">
    ///     The guard's own rationale, appended to every violator's reason. It
    ///     used to hang off the single "the set must be empty" assertion; with
    ///     one assertion per violator it has to travel with each of them or it
    ///     is lost.
    /// </param>
    internal static async Task ReportAsync(IReadOnlyList<string> violations, string? reason = null)
    {
        using var scope = Assert.Multiple();

        foreach (string violation in violations)
        {
            await Assert.That(violations.Count).IsEqualTo(0)
                .Because(reason is null ? violation : $"{violation}: {reason}");
        }
    }
}
