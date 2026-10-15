using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     CAF-F (#1288): <c>storage</c> verb on ConsoleAppFramework. Handler
///     method only — the ONE <c>RunAsync</c> call site stays in
///     <see cref="CafPilotVerbs" /> (single RunAsync per assembly: the
///     generator keys its emitted file without a per-site disambiguator, so
///     two builder sites collide with CS8785 — see the pilot remarks).
///     Moved out of the LB2 pilot (#1235) so the issue owning the verb owns
///     the file; <c>version</c> stays where it was. The handler delegates to
///     <see cref="HelpVerbs" />, so output stays byte-identical;
///     <c>-h|--help</c> comes from the framework.
/// </summary>
/// <remarks>
///     The positional tail is declared and ignored exactly like the manual
///     dispatch did (<c>"storage" =&gt; HelpVerbs.PrintStorageOptions()</c> —
///     the tail never reached the printer), and exactly like
///     <c>CafSetupCommands.Setup</c> (CAF-E) already does: without it a bare
///     extra positional (<c>harbor storage foo</c>) fails closed at the
///     parser (exit 1) instead of printing the backends (exit 0). An unknown
///     <c>--flag</c> still fails closed at the parser (exit 1) — the same
///     deferral family as CAF-A/B/D/E. Bare + extra-positional paths are
///     byte-identical (see <c>CafPilotTests</c>).
/// </remarks>
internal static class CafStorageCommands
{
    /// <summary>Print the available session storage backends.</summary>
    /// <param name="args">Positional tail (ignored, as the manual dispatch did).</param>
    public static int Storage([Argument]params string[] args) => HelpVerbs.PrintStorageOptions();
}
