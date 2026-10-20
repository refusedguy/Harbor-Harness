using TUnit.Assertions.Exceptions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #1859 control: the reporting step the converted guards use
///     (<see cref="GuardViolations" />, called by
///     <see cref="ResourceTypeTests" /> and
///     <see cref="ThemeTokenDuplicationGuardTests" />) has to name EVERY
///     violator in a single run, not only the first one.
/// </summary>
public class GuardViolationsTests
{
    /// <summary>
    ///     Twelve, not two: TUnit's collection preview truncates at ten items,
    ///     so the pre-#1859 <c>Assert.That(violations).IsEmpty()</c> could name
    ///     at most ten violators and summarised the rest as "and N more".
    ///     Twelve violators make that truncation observable: the old form
    ///     cannot name the eleventh, this one names all twelve.
    /// </summary>
    private const int ViolatorCount = 12;

    [Test]
    public async Task Every_Violator_Is_Named_In_One_Run()
    {
        string[] violators = Enumerable.Range(1, ViolatorCount)
            .Select(i => $"violator-{i:D2}")
            .ToArray();

        // Caught by hand rather than through Assert.ThrowsAsync so the assertion
        // can be on the message: the condition is not "did it throw", it is
        // "did the report name every violator".
        string report = string.Empty;
        try
        {
            await GuardViolations.ReportAsync(violators);
        }
        catch (AssertionException ex)
        {
            report = ex.Message;
        }

        await Assert.That(report).IsNotEmpty()
            .Because("a guard with violators to report has to fail");

        foreach (string violator in violators)
        {
            await Assert.That(report).Contains(violator)
                .Because("one CI run must name every violator, not only the first");
        }
    }
}
