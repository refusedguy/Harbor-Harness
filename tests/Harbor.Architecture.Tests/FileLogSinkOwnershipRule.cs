// FileLogSinkOwnershipRule.cs — GUARD for issue #558.
//
// THE CONVENTION BEING ENFORCED
// ------------------------------
// A `Microsoft.Extensions.Logging.ILoggerProvider` implementation is a
// LOGGING PROVIDER: the thing an app registers to get its log lines somewhere.
// The repo has exactly one provider that writes a per-run file under
// `~/.harbor/logs/`, and exactly one that feeds the in-memory diagnostics
// panel. A provider's simple NAME therefore has to identify exactly one
// declaration across `src/` + `apps/`.
//
// #558 found the other shape. `apps/Harbor.App.Avalonia/Logging/` held
// `FileLoggerProvider`, `HarborLogManager` and `RollingLogCleaner` — 479 lines
// byte-identical to `apps/Harbor.App.Cli/Logging/`'s copies apart from the
// namespace — and NOTHING outside that directory named any of them. The
// Avalonia app logs through Serilog (`Harbor.Logging.LoggerSetup`, wired at
// `Hosting/LoggingConfiguration.cs:29` and `:55`); this hand-rolled provider
// was compiled into the app and registered by nobody, ever.
//
// The failure mode is specific and worth naming: because the two copies shared
// a SIMPLE NAME, `rg FileLoggerProvider` never showed the dead one as
// unreferenced. Every hit outside the dead file was the CLI's own copy. A
// name-collision is what made 479 lines of dead code look maintained, and it
// is why the rule below is about duplicate NAMES rather than about a
// reachability count.
//
// SO: NO TWO PROVIDERS SHARE A NAME. Not "every provider is reachable" — that
// is not checkable by name here, and a rule that cannot fail is worse than no
// rule (see the REACHABILITY IS NOT CHECKABLE BY NAME section below).
//
// WHAT THIS RULE DELIBERATELY DOES NOT DEMAND
// --------------------------------------------
// It does NOT demand that `Harbor.Logging.LoggerSetup` (Serilog, the Avalonia
// path) be folded into the CLI's hand-rolled provider, even though both end up
// writing `harbor-*.log` into `~/.harbor/logs/`. Those two are not three copies
// of one behaviour that happen to live in different files — they are two
// different requirements that happen to share a goal:
//
//   * different level model   — the hand-rolled provider speaks `LogLevel`
//     (Trace..None, and #563 gave it the 4-char mnemonic table that answers for
//     `LogLevel.None`); Serilog speaks `LogEventLevel` (Verbose..Fatal).
//   * different line format   — the hand-rolled one renders via
//     `LogRow.FormatForFile`; Serilog uses its own
//     `{Timestamp:HH:mm:ss.fff} [{Level:u4}] [{ThreadId,3}] {SourceContext}…`.
//   * different exception rendering — the hand-rolled one writes ONE
//     `Exception:` line plus a single `Inner:` line, so a third-level inner
//     exception is dropped; `{Exception}` renders the whole chain.
//   * different retention owner — the hand-rolled app deletes through
//     `RollingLogCleaner`, Serilog through `retainedFileCountLimit` AND
//     `LoggerSetup.CleanupOldLogs`.
//
// Worse, they are DISJOINT: no single process loads both. The CLI never touches
// Serilog, the Avalonia app never touches the hand-rolled provider. So there is
// no run in which one log line could be produced twice, and unifying them would
// be a rewrite of two working log formats — a behaviour change, not a
// deduplication. Under the #555 feature freeze it is not this PR's job, and
// asserting it here would encode a rewrite as an invariant. #558's own
// "Done looks like" line `rg 'HarborLogManager|FileLoggerProvider|
// RollingLogCleaner' apps/Harbor.App.Avalonia` returning nothing IS this rule.
//
// REACHABILITY IS NOT CHECKABLE BY NAME
// ------------------------------------
// "Every provider must be named from another project" looks like the stronger
// rule and is not, because the duplicate defeats it: the dead Avalonia copy IS
// named from `apps/Harbor.App.Cli/Commands/LogsCommand.cs` and
// `src/Harbor.Ui.Framework.Projection/Projection/PanelRows.cs`, in prose about
// "the FileLogger copies" and in `<see cref="…"/>` doc comments. Counting
// comments makes the dead copy look referenced; stripping them makes the LIVE
// copy look unreferenced (its own callers all use `var`). A rule that passes
// for the wrong reason is not a rule, so the duplicate-name rule is the one
// enforced here. Deleting the dead copy is what makes the reachability question
// meaningful again.
//
// PERIMETER
// ---------
// `ScanRoots` is `src` + `apps` ONLY. `contrib/` holds more logging code but is
// outside CI and outside support by owner decision (AGENTS.md), so it is
// neither scanned nor expected to be clean. That gap is deliberate.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan really walked a checkout, read files, and really
//      found the providers it is supposed to grade. A probe that finds nothing
//      would satisfy "no duplicate names" trivially.
//   2. NonVacuity_Scan_DetectsASecondProviderInSyntheticSource — THE POSITIVE
//      CONTROL. The probe is handed synthetic sources holding a same-named
//      provider in two files, a same-named NON-provider, a provider-shaped name
//      in a comment, and an `ILogger` (not `ILoggerProvider`) implementation. It
//      MUST report the first pair as a duplicate and MUST NOT report the rest.

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One <see cref="Microsoft.Extensions.Logging.ILoggerProvider" /> declaration the probe found.</summary>
/// <param name="Name">The declared simple type name.</param>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line of the type keyword.</param>
internal sealed record ProviderDeclaration(string Name, string File, int Line);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="Declarations">Every provider declaration found, in scan order.</param>
/// <param name="DuplicateNames">
///     Simple names declared more than once, sorted — a non-empty list is the #558 failure.
/// </param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
internal sealed record ProviderScanReport(
    IReadOnlyList<ProviderDeclaration> Declarations,
    IReadOnlyList<string> DuplicateNames,
    int FilesScanned);

/// <summary>Finds <c>ILoggerProvider</c> implementations across the tree.</summary>
internal static partial class FileLogSinkProbe
{
    /// <summary>
    ///     How many lines after the type keyword a base list may span before the
    ///     scan gives up on the declaration. A base list longer than this is not
    ///     written anywhere in this repo; the bound just stops the scan running
    ///     off into a method body and inventing a provider.
    /// </summary>
    internal const int BaseListLineLookahead = 3;

    /// <summary>Repository roots the scan walks. <c>contrib/</c> is excluded on purpose.</summary>
    private static readonly string[] ScanRoots = ["src", "apps"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", "external", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static ProviderScanReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new ProviderScanReport([], [], 0);
        }

        var sources = new List<(string Relative, string[] Lines)>();
        foreach (string file in EnumerateSources(repoRoot))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            sources.Add((MakeRelative(repoRoot, file), lines));
        }

        return ScanFiles(sources);
    }

    /// <summary>
    ///     Grades already-read sources. Exposed so the positive control drives the
    ///     REAL matcher (comment stripping included) instead of a second
    ///     implementation of it, which is the only way "it can fail" means
    ///     anything.
    /// </summary>
    internal static ProviderScanReport ScanFiles(List<(string Relative, string[] Lines)> sources)
    {
        var declarations = new List<ProviderDeclaration>();
        foreach (var source in sources)
        {
            declarations.AddRange(FindDeclarations(source.Relative, source.Lines));
        }

        var duplicateNames = new List<string>();
        foreach (var group in declarations
                     .GroupBy(d => d.Name, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            duplicateNames.Add($"{group.Key} x{group.Count()}");
        }

        return new ProviderScanReport(declarations, duplicateNames, sources.Count);
    }

    /// <summary>
    ///     Scans already-read lines for provider declarations. Comments are
    ///     stripped first, so the prose in this file — and the XML docs that name
    ///     <c>FileLoggerProvider</c> — cannot be mistaken for a declaration.
    /// </summary>
    internal static List<ProviderDeclaration> FindDeclarations(string relativeFile, string[] lines)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);
        var found = new List<ProviderDeclaration>();

        for (int i = 0; i < clean.Length; i++)
        {
            foreach (Match match in TypeKeyword().Matches(clean[i]))
            {
                if (!DeclaresProvider(clean, i, match.Length))
                {
                    continue;
                }

                found.Add(new ProviderDeclaration(match.Groups["name"].Value, relativeFile, i + 1));
            }
        }

        return found;
    }

    /// <summary>
    ///     Whether the base list starting after a type name names
    ///     <c>ILoggerProvider</c>. The base list may wrap onto the next few
    ///     lines, so the walk continues until it hits a body brace, a statement
    ///     terminator, or the lookahead bound.
    /// </summary>
    private static bool DeclaresProvider(string[] clean, int declLine, int nameEnd)
    {
        for (int k = declLine; k < Math.Min(declLine + 1 + BaseListLineLookahead, clean.Length); k++)
        {
            string segment = k == declLine ? clean[declLine][nameEnd..] : clean[k];

            int brace = segment.IndexOf('{');
            if (brace >= 0)
            {
                segment = segment[..brace];
            }

            if (segment.Contains(';'))
            {
                return false;
            }

            if (ProviderName().IsMatch(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (string root in ScanRoots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                if (SkippedDirectories.Any(dir =>
                        path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    /// <summary>
    ///     A type declaration's keyword and name. Capturing the name and then
    ///     checking the base list separately is what lets the same helper report
    ///     the line the declaration sits on, which the failure message quotes.
    /// </summary>
    [GeneratedRegex(@"\b(?:class|struct|record)\s+(?<name>\w+)")]
    private static partial Regex TypeKeyword();

    /// <summary>The provider interface as a bounded word, qualified-name tolerant.</summary>
    [GeneratedRegex(@"\bILoggerProvider\b")]
    private static partial Regex ProviderName();

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard for issue #558: no <c>ILoggerProvider</c> implementation is declared
///     twice, so a logging provider that nobody registers cannot hide behind a
///     second copy carrying the same name.
/// </summary>
public sealed class FileLogSinkOwnershipRule
{
    private static readonly Lazy<ProviderScanReport> Report = new(
        () => FileLogSinkProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     Exactly one file under <c>src/</c> + <c>apps/</c> declares any given
    ///     logging provider. #558 shipped 479 lines of a second
    ///     <c>FileLoggerProvider</c> that no Avalonia code path ever registered;
    ///     it was invisible precisely because the CLI's live copy answered every
    ///     search for that name.
    /// </summary>
    [Test]
    public async Task LoggingProvider_IsDeclaredInExactlyOnePlacePerName()
    {
        var duplicates = Report.Value.DuplicateNames;

        await Assert.That(duplicates).IsEmpty()
            .Because(
                "a logging provider's simple name must identify ONE declaration. A second one is a "
                + "copy nobody can find: #558 left 479 byte-identical lines in "
                + "apps/Harbor.App.Avalonia/Logging/ that no Avalonia code path ever registered (the app "
                + "logs through Serilog via Harbor.Logging.LoggerSetup), and because the live CLI copy "
                + "carried the SAME name, every search for it matched that copy instead — so the dead "
                + "one read as maintained. Delete the unreachable copy; do not keep both in step. "
                + "Duplicated: " + (duplicates.Count == 0 ? "(none)" : string.Join(" | ", duplicates))
                + ". All declarations found: " + Describe(Report.Value.Declarations));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really found providers to grade.
    ///     "No duplicate names" is trivially satisfied by a scan that finds
    ///     nothing, so the inventory itself is asserted.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero declarations "
                   + "and the rule is satisfied by having nothing to look at");

        ProviderScanReport report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/ or apps/; the roots or the file filter are "
                   + "wrong and the rule is vacuously green");

        await Assert.That(report.Declarations.Count).IsGreaterThan(0)
            .Because("the scan found no ILoggerProvider implementation at all, so rule 1 is passing "
                   + "because the probe found nothing to grade. If the provider was renamed or its "
                   + "declaration shape changed, update FileLogSinkProbe in the same commit — do not "
                   + "delete the row.");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed five synthetic sources and
    ///     MUST report the duplicate pair while ignoring the three decoys. A probe
    ///     whose matchers stopped matching would report nothing, and the rule would
    ///     go green while enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsASecondProviderInSyntheticSource()
    {
        // Two files declaring the same provider name — the #558 shape.
        string first = """
            namespace Harbor.App.A.Logging;

            public sealed class DupSink : Microsoft.Extensions.Logging.ILoggerProvider
            {
                public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => throw new NotImplementedException();
                public void Dispose() { }
            }

            public sealed class UniqueSink : Microsoft.Extensions.Logging.ILoggerProvider
            {
                public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => throw new NotImplementedException();
                public void Dispose() { }
            }
            """;

        string second = """
            namespace Harbor.App.B.Logging;

            public sealed class DupSink : Microsoft.Extensions.Logging.ILoggerProvider
            {
                public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => throw new NotImplementedException();
                public void Dispose() { }
            }
            """;

        // A same-named class that is NOT a provider must not join the duplicate
        // count — the rule grades providers, not type names in general.
        string sameNameNotAProvider = """
            namespace Harbor.App.B.Logging;

            public sealed class DupSink : IDisposable
            {
                public void Dispose() { }
            }
            """;

        // A provider-shaped name that appears only in a doc comment is prose,
        // not a declaration.
        string commentOnly = """
            namespace Harbor.App.B.Logging;

            /// <summary>Writes a per-run file. See <c>DupSink</c> and <c>UniqueSink</c>.</summary>
            public sealed class Unrelated : IDisposable
            {
                public void Dispose() { }
            }
            """;

        // An `ILogger` (not `ILoggerProvider`) implementation is the nested logger
        // every provider holds; the bounded word must not grade it as one.
        string loggerNotProvider = """
            namespace Harbor.App.B.Logging;

            public sealed class PlainLogger : Microsoft.Extensions.Logging.ILogger
            {
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => throw new NotImplementedException();
                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
            }
            """;

        ProviderScanReport report = FileLogSinkProbe.ScanFiles(
        [
            ("apps/Harbor.App.A/Logging/DupSink.cs", SplitLines(first)),
            ("apps/Harbor.App.B/Logging/DupSink.cs", SplitLines(second)),
            ("apps/Harbor.App.B/Logging/Disposable.cs", SplitLines(sameNameNotAProvider)),
            ("apps/Harbor.App.B/Logging/Unrelated.cs", SplitLines(commentOnly)),
            ("apps/Harbor.App.B/Logging/PlainLogger.cs", SplitLines(loggerNotProvider)),
        ]);

        await Assert.That(string.Join(" | ", report.DuplicateNames)).IsEqualTo("DupSink x2")
            .Because("the first two snippets declare the same ILoggerProvider name in two files, which is "
                   + "precisely the #558 shape rule 1 must reject. A miss means the type-keyword matcher, "
                   + "the base-list walk or the grouping stopped working, and the rule enforces nothing. "
                   + "Reported: " + (report.DuplicateNames.Count == 0
                       ? "(nothing)"
                       : string.Join(" | ", report.DuplicateNames)));

        string[] names = [.. report.Declarations.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal)];

        await Assert.That(string.Join(" | ", names)).IsEqualTo("DupSink | DupSink | UniqueSink")
            .Because("the scan must find BOTH DupSink declarations plus the uniquely-named provider, and "
                   + "nothing else: the third snippet's `DupSink : IDisposable` is not a provider, the "
                   + "fourth names DupSink only in a doc comment, and the fifth implements ILogger rather "
                   + "than ILoggerProvider. Scanned: " + string.Join(" | ", names));

        await Assert.That(report.Declarations.Count(d => d.File == "apps/Harbor.App.B/Logging/Disposable.cs"))
            .IsEqualTo(0)
            .Because("a type that implements IDisposable is not a logging provider, and counting it would "
                   + "make the duplicate rule fire on an unrelated same-name class");
    }

    /// <summary>Splits a raw string literal into lines the way <c>File.ReadAllLines</c> would.</summary>
    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Renders declarations as <c>Name at file:line</c> for a failure message.</summary>
    private static string Describe(IReadOnlyList<ProviderDeclaration> declarations) =>
        declarations.Count == 0
            ? "(nothing)"
            : string.Join(" | ", declarations
                .OrderBy(d => d.File, StringComparer.Ordinal)
                .ThenBy(d => d.Line)
                .Select(d => $"{d.Name} at {d.File}:{d.Line}"));
}
