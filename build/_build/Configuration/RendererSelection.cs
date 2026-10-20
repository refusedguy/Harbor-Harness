namespace Harbor.Build.Configuration;

/// <summary>
///     Build-time renderer-backend selection for <c>HarborWithRenderer</c>
///     (slice A, #1144). NUKE exposes it as <c>--with-renderer</c>;
///     the CLI build configurator lowercases the name into the
///     MSBuild property the projects switch on
///     (<c>all|cellforge|ansiplain</c>).
/// </summary>
/// <remarks>
///     The vocabulary anticipates the full single-renderer CLI. Today it
///     excludes NOTHING: CellForge drives <c>ask</c>/REPL and AnsiPlain drives
///     plain pipes, so neither leaves the CLI graph yet — see docs/BUILD.md.
///     The one backend this flag COULD leave out was the vendored SharpConsoleUI
///     renderer, and that backend left the product for <c>contrib/tui/</c>: it is
///     in no solution, no CI job compiles it, and the product does not reference
///     it. Accepting the full vocabulary now keeps the flag forward-compatible:
///     unknown values are rejected both here (NUKE parses the enum) and in
///     MSBuild (the Error guard in <c>Harbor.Hosting.csproj</c> catches direct
///     <c>-p:</c> misspellings).
/// </remarks>
public enum RendererSelection
{
    /// <summary>Compose every backend (status quo, default).</summary>
    All,

    /// <summary>Interactive cell-diff backend family.</summary>
    CellForge,

    /// <summary>ANSI-streaming + plain-pipe backend family.</summary>
    AnsiPlain,
}
