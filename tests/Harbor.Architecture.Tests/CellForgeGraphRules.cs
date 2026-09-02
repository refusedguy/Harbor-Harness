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

        await Assert.That(loaded.TryGetValue("Harbor.Tui.CellForge", out var consoleEx)).IsTrue()
            .Because("Harbor.Tui.CellForge must be part of the loaded assembly inventory.");

        // The controller itself must live inside the CellForge assembly.
        var controller = consoleEx!.GetType(
            "Harbor.Tui.CellForge.Input.UnixTermiosModeController", throwOnError: false);
        await Assert.That(controller).IsNotNull()
            .Because("UnixTermiosModeController must remain a CellForge-internal detail.");

        // Only the CellForge assembly itself, the composition root and the
        // Hosting composition module (TuiModule registers the Phase-2
        // CellForgeTuiRenderer adapter) may reference it. Every other Harbor
        // assembly referencing the assembly is a layering leak of the termios
        // graph.
        HashSet<string> allowed =
        [
            "Harbor.Tui.CellForge", // self
            "Harbor.App.Cli",       // composition root: CellForgeReplRunner wiring
            "Harbor.Hosting"        // TuiModule: CellForgeTuiRenderer registration
        ];

        var violations = new List<string>();
        foreach (var (name, asm) in loaded)
        {
            if (allowed.Contains(name))
            {
                continue;
            }

            var refs = ArchitectureTestHelpers.GetReferencedAssemblyNames(asm);
            if (refs.Contains("Harbor.Tui.CellForge"))
            {
                violations.Add(name);
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because($"Only {string.Join(", ", allowed)} may reference Harbor.Tui.CellForge; found: {string.Join(", ", violations)}");
    }
}
