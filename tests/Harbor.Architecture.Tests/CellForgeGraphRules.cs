namespace Harbor.Architecture.Tests;

/// <summary>
///     CE-5 Зона 3 — граф-правило для termios-слоя CellForge.
///     <see cref="Harbor.Tui.CellForge.Input.UnixTermiosModeController" /> (прямой
///     termios P/Invoke) и его контракт <c>ITerminalModeController</c> —
///     деталь реализации CellForge-рендерера: composition root (Harbor.App.Cli)
///     их подключает, больше НИКТО их трогать не должен. Наружу торчит только
///     <c>Harbor.Tui.CellForge</c>-сборка целиком; любой новый потребитель
///     обязан сначала добавить ссылку на эту сборку — что этот тест и ловит.
/// </summary>
/// <remarks>
///     Проверка на уровне ссылок между сборками: чтобы использовать
///     UnixTermiosModeController вне CellForge, сборка обязана сослаться на
///     Harbor.Tui.CellForge — этого достаточно, чтобы поймать утечку графа.
///     contrib/* сознательно вне скоупа (см. LayerDependencyTests).
/// </remarks>
public sealed class CellForgeGraphRules
{
    [Test]
    public async Task UnixTermiosModeController_ConfinedToCellForgeGraph()
    {
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();

        await Assert.That(loaded.TryGetValue("Harbor.Tui.CellForge.Engine", out var engine)).IsTrue()
            .Because("Harbor.Tui.CellForge.Engine must be part of the loaded assembly inventory.");

        // The controller itself must live inside the CellForge.Engine assembly
        // (issue #33 split; namespace unchanged by design).
        var controller = engine!.GetType(
            "Harbor.Tui.CellForge.Input.UnixTermiosModeController", throwOnError: false);
        await Assert.That(controller).IsNotNull()
            .Because("UnixTermiosModeController must remain a CellForge-internal detail.");

        // Only the CellForge assemblies themselves, the composition root and
        // the Hosting composition module (TuiModule registers the Phase-2
        // CellForgeTuiRenderer adapter) may reference them. Every other Harbor
        // assembly referencing the engine assembly is a layering leak of the
        // termios graph.
        HashSet<string> allowed =
        [
            "Harbor.Tui.CellForge.Engine", // self
            "Harbor.Tui.CellForge",        // chat shell over the engine
            "Harbor.App.Cli",              // composition root: CellForgeReplRunner wiring
            "Harbor.Hosting"               // TuiModule: CellForgeTuiRenderer registration
        ];

        var violations = new List<string>();
        foreach (var (name, asm) in loaded)
        {
            if (allowed.Contains(name))
            {
                continue;
            }

            var refs = ArchitectureTestHelpers.GetReferencedAssemblyNames(asm);
            if (refs.Contains("Harbor.Tui.CellForge.Engine"))
            {
                violations.Add(name);
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because($"Only {string.Join(", ", allowed)} may reference Harbor.Tui.CellForge.Engine; found: {string.Join(", ", violations)}");
    }
}
