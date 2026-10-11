namespace Harbor.Build.Configuration;

/// <summary>
///     Build-time renderer-backend selection for <c>HarborWithRenderer</c>
///     (slice A, #1144). NUKE exposes it as <c>--with-renderer</c>;
///     the CLI build configurator lowercases the name into the
///     MSBuild property the projects switch on
///     (<c>all|cellforge|ansiplain|nickconsoleex</c>).
/// </summary>
/// <remarks>
///     The vocabulary anticipates the full single-renderer CLI: today only
///     the <c>NickConsoleEx</c> exclusion is enforced (CellForge drives
///     <c>ask</c>/REPL and AnsiPlain drives plain pipes, so neither leaves
///     the CLI graph yet — see docs/BUILD.md). Accepting the full vocabulary
///     now keeps the flag forward-compatible: unknown values are rejected
///     both here (NUKE parses the enum) and in MSBuild (the Error guards in
///     <c>Harbor.Hosting.csproj</c> catch direct <c>-p:</c> misspellings).
/// </remarks>
public enum RendererSelection
{
    /// <summary>Compose every backend (status quo, default).</summary>
    All,

    /// <summary>Interactive cell-diff backend family.</summary>
    CellForge,

    /// <summary>ANSI-streaming + plain-pipe backend family.</summary>
    AnsiPlain,

    /// <summary>
    ///     SharpConsoleUI wrapper backend. Requires the vendored ConsoleEx
    ///     submodule — the build fails otherwise (MSBuild guard + doctor
    ///     <c>renderer.deps</c> check).
    /// </summary>
    NickConsoleEx
}
