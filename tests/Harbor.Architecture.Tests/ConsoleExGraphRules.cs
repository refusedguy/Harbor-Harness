namespace Harbor.Architecture.Tests;

/// <summary>
///     CE-5 Зона 3 — граф-правило для termios-слоя ConsoleEx.
///     <see cref="Harbor.Tui.ConsoleEx.Input.UnixTermiosModeController" /> (прямой
///     termios P/Invoke) и его контракт <c>ITerminalModeController</c> —
///     деталь реализации ConsoleEx-рендерера: composition root (Harbor.App.Cli)
///     их подключает, больше НИКТО их трогать не должен. Наружу торчит только
///     <c>Harbor.Tui.ConsoleEx</c>-сборка целиком; любой новый потребитель
///     обязан сначала добавить ссылку на эту сборку — что этот тест и ловит.
/// </summary>
/// <remarks>
///     Проверка на уровне ссылок между сборками: чтобы использовать
///     UnixTermiosModeController вне ConsoleEx, сборка обязана сослаться на
///     Harbor.Tui.ConsoleEx — этого достаточно, чтобы поймать утечку графа.
///     contrib/* сознательно вне скоупа (см. LayerDependencyTests).
/// </remarks>
public sealed class ConsoleExGraphRules
{
    [Test]
    public async Task UnixTermiosModeController_ConfinedToConsoleExGraph()
    {
        var loaded = ArchitectureTestHelpers.LoadHarborAssemblies();

        await Assert.That(loaded.TryGetValue("Harbor.Tui.ConsoleEx", out var consoleEx)).IsTrue()
            .Because("Harbor.Tui.ConsoleEx must be part of the loaded assembly inventory.");

        // The controller itself must live inside the ConsoleEx assembly.
        var controller = consoleEx!.GetType(
            "Harbor.Tui.ConsoleEx.Input.UnixTermiosModeController", throwOnError: false);
        await Assert.That(controller).IsNotNull()
            .Because("UnixTermiosModeController must remain a ConsoleEx-internal detail.");

        // Only the ConsoleEx assembly itself and the composition root may
        // reference it. Every other Harbor assembly referencing the assembly
        // is a layering leak of the termios graph.
        HashSet<string> allowed =
        [
            "Harbor.Tui.ConsoleEx", // self
            "Harbor.App.Cli"        // composition root: ConsoleExReplRunner wiring
        ];

        var violations = new List<string>();
        foreach (var (name, asm) in loaded)
        {
            if (allowed.Contains(name))
            {
                continue;
            }

            var refs = ArchitectureTestHelpers.GetReferencedAssemblyNames(asm);
            if (refs.Contains("Harbor.Tui.ConsoleEx"))
            {
                violations.Add(name);
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because($"Only {string.Join(", ", allowed)} may reference Harbor.Tui.ConsoleEx; found: {string.Join(", ", violations)}");
    }
}
