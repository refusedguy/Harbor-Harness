using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.DesignSystem.Tests;

/// <summary>
/// Package-surface contract (sprint "Design System as Product"): the
/// Harbor.DesignSystem assembly is a standalone NuGet package — its IL must
/// reference ZERO Harbor.* assemblies, so any consumer referencing only this
/// package pulls in nothing else.
/// </summary>
[NotInParallel("terminal-color-palette")]
public class StandalonePackageTests
{
    [Test]
    public async Task DesignSystemAssembly_HasZeroHarborAssemblyReferences()
    {
        var refs = typeof(HarborTheme).Assembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null && a.Name.StartsWith("Harbor", StringComparison.Ordinal))
            .Select(a => a.Name!)
            .ToList();

        await Assert.That(refs).IsEmpty()
            .Because("Harbor.DesignSystem must be standalone; found: " + string.Join(", ", refs));
    }

    [Test]
    public async Task TokenSurface_IsReachableFromSingleReference()
    {
        // Smoke the shipped API surface that the package promises.
        await Assert.That(DesignTokens.Space8).IsEqualTo(8);
        await Assert.That(HarborTheme.BuiltIn).Count().IsGreaterThanOrEqualTo(3);
        await Assert.That(TerminalColorPalette.Current).IsEqualTo(HarborTheme.HarborDark);
    }
}
