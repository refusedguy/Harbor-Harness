using Harbor.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;

namespace Harbor.Hosting.Tests;

/// <summary>
///     #1523: <c>IWorkspaceContextSource</c> is registered in the composition
///     root as a singleton, and it is the very instance the static migration
///     facade (<c>WorkspaceContextSource.Instance</c>) forwards to — so the
///     DI path and the pre-DI static path share one workspace cache.
/// </summary>
[NotInParallel("hosting")]
public class WorkspaceContextSourceCompositionTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-hosting-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task AddHarbor_WorkspaceContextSource_SingletonSharedWithStaticFacade()
    {
        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = TempHarborDir(),
            DefaultStorageBackend = "memory",
        });
        using var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<IWorkspaceContextSource>();
        var second = sp.GetRequiredService<IWorkspaceContextSource>();

        // Singleton lifetime...
        await Assert.That(first).IsSameReferenceAs(second);
        // ...and one workspace cache per process, not two.
        await Assert.That(first).IsSameReferenceAs(WorkspaceContextSource.Instance);
    }
}
