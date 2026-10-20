using Harbor.App.Cli.Hosting;
using Harbor.TestKit;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Bring-up slice 2 (#1249): CellForge raw mode is DEFAULT ON on Windows.
///     <c>HARBOR_CELLFORGE_WINDOWS</c> was an opt-in in slice 1; it is an
///     opt-out now (<c>0</c>/<c>false</c> keeps the legacy renderer there), so
///     the unset state — the one a fresh install has — must reach the Win32
///     controller instead of the legacy fallback.
/// </summary>
/// <remarks>
///     <para>
///         Parsing is platform-independent and so is the default-on rule
///         itself: <see cref="TuiMode.IsCellForgeBlocked" /> takes the platform
///         bit as a value, so "Windows + unset ⇒ not blocked" is asserted on
///         the Linux CI runner too rather than only on the Windows leg.
///     </para>
///     <para>
///         Один метод вместо нескольких: мутация процессного env var не должна
///         interleav'иться с параллельными тестами даже внутри класса — тот же
///         резон, что у <c>CellForgeModuleApproverTests</c>.
///     </para>
/// </remarks>
[NotInParallel("process-env")]
public class CellForgeWindowsDefaultOnTests
{
    [Test]
    public async Task WindowsDefault_IsOn_AndOnlyOptOutBlocks()
    {
        using var optOut = new EnvScope(TuiMode.WindowsCellForgeOptOutVariable);
        // The default-on contract: Windows with the variable unset is no
        // longer a blocked platform. This is the state a fresh install is
        // in, and the state slice 1 turned into the legacy fallback.
        optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, null);
        await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: true)).IsFalse()
            .Because("a fresh install must reach the Windows VT raw-mode controller, not the legacy fallback.");

        // Slice-1 spellings meant CellForge and still do.
        foreach (string optedIn in new[] { "1", "true", "True", "TRUE", " 1 " })
        {
            optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, optedIn);
            await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: true)).IsFalse()
                .Because($"'{optedIn}' must not block the Windows controller.");
        }

        // Only an explicit off keeps the legacy renderer on Windows.
        foreach (string optedOut in new[] { "0", "false", "False", "FALSE", " false " })
        {
            optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, optedOut);
            await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: true)).IsTrue()
                .Because($"'{optedOut}' is the documented way to keep legacy on Windows.");
        }

        // Anything else is not an instruction — leave the default alone.
        foreach (string? neither in new[] { "off", "no", "yes", string.Empty })
        {
            optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, neither);
            await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: true)).IsFalse()
                .Because($"'{neither ?? "(empty)"}' is not an opt-out and must not be read as one.");
        }

        // Off Windows the platform raw-mode controller is established, so
        // the gate never fires regardless of the flag.
        optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, null);
        await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: false)).IsFalse();
        optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, "0");
        await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: false)).IsFalse();
        optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, "1");
        await Assert.That(TuiMode.IsCellForgeBlocked(platformIsWindows: false)).IsFalse();

        // The ambient overload agrees with the pure one on this host: on
        // Windows the unset state is unblocked, off Windows it always is.
        optOut.Set(TuiMode.WindowsCellForgeOptOutVariable, null);
        await Assert.That(TuiMode.IsCellForgeBlockedOnThisOs()).IsFalse()
            .Because("this host must not be a blocked platform with the variable unset.");
    }
}
