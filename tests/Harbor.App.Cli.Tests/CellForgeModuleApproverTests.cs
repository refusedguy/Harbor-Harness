using Harbor.App.Cli.Hosting;
using Harbor.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #52: без доступного аппрувера (one-shot verbs, перенаправленный stdin)
///     CellForge не должен подменять fail-closed <c>PermissionService</c>
///     интерактивным asker'ом — иначе Ask-вызовы висят вечно.
/// </summary>
public class CellForgeModuleApproverTests
{
    /// <summary>
    ///     Один метод вместо трёх: мутация процессного env var не должна
    ///     interleav'иться с параллельными тестами даже внутри класса.
    /// </summary>
    [Test]
    public async Task NoApproverMarker_Disables_InteractiveOverride()
    {
        string? prev = Environment.GetEnvironmentVariable("HARBOR_NO_APPROVER");
        try
        {
            global::Harbor.App.Cli.Program.MarkApproverless();
            await Assert.That(Environment.GetEnvironmentVariable("HARBOR_NO_APPROVER")).IsEqualTo("1");
            await Assert.That(Hosting.CellForgeModule.IsApprovalPromptAvailable()).IsFalse();

            var services = new ServiceCollection();
            services.AddCellForge(CellForgeUiConfig.Default);
            using var provider = services.BuildServiceProvider();
            await Assert.That(provider.GetService<Repl.CellForgePermissionAsker>()).IsNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARBOR_NO_APPROVER", prev);
        }
    }
}
