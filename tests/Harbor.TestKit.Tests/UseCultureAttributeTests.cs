using System.Globalization;
using Harbor.TestKit;

namespace Harbor.TestKit.Tests;

/// <summary>
///     #2704 T05: the shared culture fixture has to do two things a test body
///     cannot guarantee on its own — pin the culture for the annotated test,
///     and hand the ambient values back for whatever runs next. The
///     <c>DependsOn</c> chain makes the restoration observable instead of
///     hoping the scheduler puts the unannotated test after the annotated one.
/// </summary>
/// <remarks>
///     Culture is async-flow state shared by the test process, so this class
///     takes the bare <c>NotInParallel</c> lock: a concurrent culture swap in
///     the same project would make the baseline useless.
/// </remarks>
[NotInParallel]
public sealed class UseCultureAttributeTests
{
    private static string _ambientCulture = string.Empty;
    private static string _ambientUiCulture = string.Empty;

    [Test]
    public async Task Baseline_RecordsAmbientCulture()
    {
        _ambientCulture = CultureInfo.CurrentCulture.Name;
        _ambientUiCulture = CultureInfo.CurrentUICulture.Name;

        await Assert.That(_ambientCulture).IsNotEqualTo("tr-TR")
            .Because("the baseline must be a culture the fixture actually changes");
    }

    [Test]
    [UseCulture("tr-TR")]
    [DependsOn(nameof(Baseline_RecordsAmbientCulture))]
    public async Task AnnotatedTest_RunsUnderRequestedCulture()
    {
        await Assert.That(CultureInfo.CurrentCulture.Name).IsEqualTo("tr-TR");
        await Assert.That(CultureInfo.CurrentUICulture.Name).IsEqualTo("tr-TR");
    }

    [Test]
    [DependsOn(nameof(AnnotatedTest_RunsUnderRequestedCulture))]
    public async Task AfterAnnotatedTest_AmbientCultureIsRestored()
    {
        await Assert.That(CultureInfo.CurrentCulture.Name).IsEqualTo(_ambientCulture)
            .Because("a culture swap must not leak into the next test in the project");
        await Assert.That(CultureInfo.CurrentUICulture.Name).IsEqualTo(_ambientUiCulture)
            .Because("CurrentUICulture is process state too and must be restored with CurrentCulture");
    }
}
