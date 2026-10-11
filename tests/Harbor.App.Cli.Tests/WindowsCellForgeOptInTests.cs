using Harbor.App.Cli.Hosting;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     PX3 slice 1 (#1249): CellForge raw mode on Windows is opt-in behind
///     <c>HARBOR_CELLFORGE_WINDOWS</c>. Parsing is platform-independent; the OS
///     half of the gate is asserted as "never blocked off Windows" so the
///     suite stays meaningful on Linux/macOS CI, while the Windows half is
///     exercised by the win leg running the Win32 controller tests.
/// </summary>
/// <remarks>
///     Один метод вместо нескольких: мутация процессного env var не должна
///     interleav'иться с параллельными тестами даже внутри класса — тот же
///     резон, что у <c>CellForgeModuleApproverTests</c>.
/// </remarks>
[NotInParallel("process-env")]
public class WindowsCellForgeOptInTests
{
    [Test]
    public async Task WindowsOptIn_Parses_And_Gates()
    {
        string? prev = Environment.GetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable);
        try
        {
            // Accepted spellings — the same 1/true shape as HARBOR_TUI_RUNTIME_SWAP.
            foreach (string optedIn in new[] { "1", "true", "True", "TRUE", " 1 " })
            {
                Environment.SetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable, optedIn);
                await Assert.That(TuiMode.IsWindowsCellForgeOptedIn()).IsTrue()
                    .Because($"'{optedIn}' must opt in.");
            }

            // Everything else — including unset — stays on the legacy fallback.
            foreach (string? optedOut in new[] { "0", "false", "False", "yes", string.Empty })
            {
                Environment.SetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable, optedOut);
                await Assert.That(TuiMode.IsWindowsCellForgeOptedIn()).IsFalse()
                    .Because($"'{optedOut}' must not opt in.");
            }

            Environment.SetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable, null);
            await Assert.That(TuiMode.IsWindowsCellForgeOptedIn()).IsFalse()
                .Because("an unset variable must not opt in.");

            // Off Windows the platform controller is established, so the gate
            // never fires regardless of the flag — the default there is unchanged.
            if (!OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable, "0");
                await Assert.That(TuiMode.IsCellForgeBlockedOnThisOs()).IsFalse();
                Environment.SetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable, "1");
                await Assert.That(TuiMode.IsCellForgeBlockedOnThisOs()).IsFalse();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TuiMode.WindowsCellForgeOptInVariable, prev);
        }
    }
}
