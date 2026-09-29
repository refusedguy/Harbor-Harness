// ResultTryAdoptionTests.cs — ROP wave: `try/catch -> Result` must ride the
// library, not be hand-rolled.
//
// CSharpFunctionalExtensions 3.7.0 ships `Result.Try(Func<T>, Func<Exception,string>)`
// plus the `Action` / `Func<Task>` / `Func<Task<T>>` shapes. It is exactly a
// try/catch that returns `Result.Failure(handler(ex))` — so
//
//     try { DoWork(); return Result.Success(); }
//     catch (Exception ex) { return Result.Failure($"...: {ex.Message}"); }
//
// is a SECOND implementation of a member the library already provides, and a
// second implementation is a drift source.
//
// WHY A SOURCE SCAN AND NOT BannedSymbols.txt: RS0030 bans *symbols* (a type
// or method name). What is banned here is a two-statement *shape* — a `catch`
// whose body constructs a `Result.Failure` — which no symbol ban can express.
// The entries in BannedSymbols.txt are framework-API shaped (Newtonsoft,
// sync-over-async, Thread.Sleep); this is a structural convention. The repo
// already runs two source-text gates in this same project
// (BenchmarkContractTests, ReadmeCoverageTests), so this follows the
// established mechanism rather than inventing one.
//
// THE GATE MUST NOT FLAG A CATCH THAT CATCHES LESS THAN `Exception`.
// `Result.Try` catches EVERY exception, so converting a narrowed catch would
// silently widen it and change behaviour. Three shapes are therefore exempt,
// and every one of them is a real site in this repo today:
//
//   1. `catch (Exception ex) when (ex is IOException or ...)` — narrowed by an
//      exception filter. Live examples: ImageAttachmentReader.cs:73, :96,
//      FileClaimRegistry.cs:149, WorkspaceInspector.cs:135, SkillUpdater.cs:273.
//      Converting these would swallow exceptions the author chose to let escape.
//   2. `catch (IOException)` / `catch (JsonException)` — narrowed by type.
//      Live examples: DaemonBindPolicy.cs:233,253; HostsCatalog.cs:87,92;
//      PluginScript.cs:124.
//   3. More than one `catch` arm — a dispatch, not a single net. `Result.Try`
//      has one handler. IdeSessionBridge.cs:186-197 routes cancellation and
//      general faults to DIFFERENT messages and must keep doing so.
//
// A fourth shape needs an explicit entry rather than a structural test: a try
// body that returns a `Result` it obtained from a CALL (not a literal
// `Result.Success`/`Result.Failure`). `Result.Try` over that is
// `Result<Result<T>>`, so it is a type-level refactor — split the call out and
// `Bind` it — not this wave's shape fix. A source scan cannot see the callee's
// return type, so those are listed in KnownExemptSites with a reason, and
// Assert_KnownExemptSitesAreStillAccurate fails if a reason stops holding.
//
// KnownExemptSites is a RATCHET, not a waiver: it may only shrink.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     ROP gate: the hand-rolled <c>try/catch -&gt; Result</c> shape must be
///     written as <c>Result.Try</c> (CSharpFunctionalExtensions 3.7.0).
/// </summary>
public class ResultTryAdoptionTests
{
    /// <summary>
    ///     Files whose existence proves the scan still covers the ROP perimeter
    ///     this wave converted. If these are renamed the scan silently covers
    ///     nothing and the gate passes vacuously.
    /// </summary>
    private static readonly string[] ScopeMarkerFiles =
    [
        Path.Combine("src", "Harbor.Storage.Jsonl", "JsonlSessionStore.cs"),
        Path.Combine("src", "Harbor.Storage.Jsonl", "JsonlLineParser.cs"),
        Path.Combine("src", "Harbor.Plugins.Instantiation", "PluginLifecycle.cs"),
        Path.Combine("src", "Harbor.Plugins.Registration", "PluginRegistrar.cs"),
        Path.Combine("src", "Harbor.Providers.OpenAiCompatible", "ProviderConfig.cs"),
    ];

    /// <summary>
    ///     A <c>try</c> opening a block. The brace sits on the NEXT line in
    ///     this codebase's style, so the pattern must not require it inline.
    /// </summary>
    private static readonly Regex TryStatement = new(
        @"^[ \t]*try[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A <c>catch</c> clause, capturing its exception type and filter.</summary>
    private static readonly Regex CatchClause = new(
        @"^[ \t]*catch[ \t]*\([ \t]*(?<type>[A-Za-z0-9_.]+)[ \t]*(?<var>\w*)[ \t]*\)(?<when>[ \t]+when[ \t]+[^\n]*)?[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    ///     A <c>return</c> of a literal <see cref="Result" /> from a try body —
    ///     the syntactic <c>Result&lt;Result&lt;T&gt;&gt;</c> case.
    /// </summary>
    private static readonly Regex NestedResultReturn = new(
        @"\breturn\s+(?:Task\.FromResult\(\s*)?Result\.(?:Success|Failure)",
        RegexOptions.Compiled);

    /// <summary>A <c>Result.Failure</c> construction inside a catch body.</summary>
    private static readonly Regex ResultFailure = new(
        @"\bResult\.Failure\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     A try body that yields a value a source scan cannot type: either
    ///     <c>return some.Callee(...)</c> (the callee's return type is invisible
    ///     to a text scan, and it may be a <c>Result</c>) or
    ///     <c>local = await Callee(...)</c> assigning an outer variable.
    /// </summary>
    private static readonly Regex AssignedOrReturnedCall = new(
        @"^\s*(?:return\s+\w+(?:\.\w+)*\s*\(|[\w\.]+\s*=\s*await\s)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    ///     Sites that match the shape but are deliberately left alone, each
    ///     with the reason the gate must not "fix" them. A source scan cannot
    ///     see a callee's return type, so a try body that returns a
    ///     <c>Result</c> obtained from a CALL lands here rather than being
    ///     classified structurally.
    /// </summary>
    private static readonly (string File, int Line, string Reason)[] KnownExemptSites =
    [
        (Path.Combine("src", "Harbor.Storage.Jsonl", "JsonlLineParser.cs"), 84,
            "try body returns Result.Failure directly (early-return validation inside try)"),
        (Path.Combine("src", "Harbor.Storage.Jsonl", "JsonlLineParser.cs"), 224, "try body returns Result directly"),
        (Path.Combine("src", "Harbor.Storage.Jsonl", "JsonlLineParser.cs"), 295, "try body returns Result directly"),
        (Path.Combine("src", "Harbor.Storage.Jsonl", "JsonlLineParser.cs"), 349, "try body returns Result directly"),
        (Path.Combine("src", "Harbor.Plugins.Registration", "SafePluginRegistrar.cs"), 39,
            "try body returns the callee's Result (IPluginRegistrar.Register) — needs a Bind split, not a shape fix"),
        // Outside this wave's perimeter (Harbor.Lsp), and NOT convertible as a
        // shape fix: the catch takes _sync and mutates _unavailable before
        // building the Result, and the try ASSIGNS to an outer `session`
        // variable rather than producing the returned value. Making it
        // Result.Try means restructuring the method's control flow, which is
        // its own change with its own review.
        (Path.Combine("src", "Harbor.Lsp", "LspManager.cs"), 287,
            "catch mutates _unavailable under a lock; try assigns an outer local — needs a control-flow refactor"),
    ];

    [Test]
    public async Task HandRolledTryCatchToResult_UsesResultTry()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            // Published/trimmed test host: nothing to scan rather than a false pass.
            return;
        }

        HashSet<string> exempt = [.. KnownExemptSites.Select(e => $"{Normalise(e.File)}:{e.Line}")];

        List<string> violations = [];
        foreach (string file in EnumerateSourceFiles(root))
        {
            violations.AddRange(ScanFile(root, file, exempt));
        }

        await Assert.That(violations)
            .IsEmpty()
            .Because(
                "hand-rolled `catch(Exception) -> Result.Failure` found — use `Result.Try` from " +
                "CSharpFunctionalExtensions 3.7.0 instead. Each site is a second implementation of a " +
                "library member and will drift: " + string.Join(" | ", violations));
    }

    [Test]
    public async Task KnownExemptSites_AreStillAccurate()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return;
        }

        List<string> stale = [];
        foreach ((string rel, int line, string reason) in KnownExemptSites)
        {
            string full = Path.Combine(root, rel);
            if (!File.Exists(full))
            {
                stale.Add($"{Normalise(rel)} — file no longer exists (delete the ratchet entry)");
                continue;
            }

            string[] lines = File.ReadAllLines(full);
            if (line > lines.Length)
            {
                stale.Add($"{Normalise(rel)}:{line} — line past end of file (update the ratchet entry)");
                continue;
            }

            // The ratchet records the CATCH line; walk up to the nearest catch.
            int idx = line - 1;
            while (idx >= 0 && !lines[idx].Contains("catch", StringComparison.Ordinal))
            {
                idx--;
            }

            if (idx < 0)
            {
                stale.Add($"{Normalise(rel)}:{line} — no catch clause at/above this line ({reason})");
                continue;
            }

            int? catchClose = FindBlockEnd(lines, idx);
            if (catchClose is null)
            {
                stale.Add($"{Normalise(rel)}:{idx + 1} — catch block does not close ({reason})");
                continue;
            }

            string catchBody = string.Join('\n', lines[(idx + 1)..catchClose.Value]);
            if (!ResultFailure.IsMatch(catchBody))
            {
                stale.Add($"{Normalise(rel)}:{idx + 1} — catch no longer constructs Result.Failure ({reason})");
                continue;
            }

            // The exemption exists because the TRY body returns a Result (either
            // a literal, or a callee's). Walk back to the owning try and re-check.
            int tryIdx = idx;
            while (tryIdx >= 0 && !lines[tryIdx].TrimStart().StartsWith("try", StringComparison.Ordinal))
            {
                tryIdx--;
            }

            if (tryIdx < 0)
            {
                stale.Add($"{Normalise(rel)}:{idx + 1} — no owning try found for this catch ({reason})");
                continue;
            }

            int? tryClose = FindBlockEnd(lines, tryIdx);
            string tryBody = tryClose is null
                ? string.Empty
                : string.Join('\n', lines[(tryIdx + 1)..tryClose.Value]);

            // The exemption holds while the try body still yields a Result —
            // either a literal `return Result.X`, a `return <callee>(...)`
            // (possibly `a.B(...)`) whose value the scan cannot type-check, or
            // an assignment to an outer local from an awaited call. A try body
            // that does none of these is a plain Result.Try candidate.
            bool stillExempt = NestedResultReturn.IsMatch(tryBody)
                               || AssignedOrReturnedCall.IsMatch(tryBody);
            if (!stillExempt)
            {
                stale.Add(
                    $"{Normalise(rel)}:{idx + 1} — the try body no longer yields a Result, so the " +
                    $"exemption no longer applies; this is now a plain Result.Try candidate ({reason})");
            }
        }

        await Assert.That(stale)
            .IsEmpty()
            .Because(
                "KnownExemptSites is a ratchet: an entry that no longer matches its recorded reason means " +
                "the exemption is stale — convert the site and delete the entry: " + string.Join(" | ", stale));
    }

    [Test]
    public async Task ScopeMarkerFiles_StillExist()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return;
        }

        List<string> missing = [.. ScopeMarkerFiles.Where(f => !File.Exists(Path.Combine(root, f)))];

        await Assert.That(missing)
            .IsEmpty()
            .Because("ROP perimeter marker files moved — update ScopeMarkerFiles: " + string.Join(", ", missing));
    }

    /// <summary>Every <c>.cs</c> file under <c>src/</c>, minus build output.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        string src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return path;
        }
    }

    /// <summary>Repo-relative, forward-slashed form used as the ratchet key.</summary>
    private static string Normalise(string relative) => relative.Replace('\\', '/');

    /// <summary>
    ///     Returns one message per violation in <paramref name="file" />, or an
    ///     empty sequence. See the file header for the exempt shapes.
    /// </summary>
    private static IEnumerable<string> ScanFile(string root, string file, HashSet<string> exempt)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException)
        {
            yield break;
        }

        string rel = Normalise(Path.GetRelativePath(root, file));
        string[] lines = text.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (!TryStatement.IsMatch(lines[i]))
            {
                continue;
            }

            int? close = FindBlockEnd(lines, i);
            if (close is null)
            {
                continue;
            }

            string tryBody = string.Join('\n', lines[(i + 1)..close.Value]);

            int catchLine = FindCatchLine(lines, close.Value + 1);
            if (catchLine < 0)
            {
                continue;
            }

            // More than one catch arm is a dispatch, not a single net.
            int firstClose = FindBlockEnd(lines, catchLine) ?? catchLine;
            int secondCatch = FindCatchLine(lines, firstClose + 1);
            if (secondCatch >= 0)
            {
                continue;
            }

            Match clause = CatchClause.Match(lines[catchLine]);
            if (!clause.Success)
            {
                continue;
            }

            // Narrowed by an exception filter, or by type: `Result.Try` would widen it.
            if (clause.Groups["when"].Success)
            {
                continue;
            }

            if (clause.Groups["type"].Value is not ("Exception" or "System.Exception"))
            {
                continue;
            }

            int? catchClose = FindBlockEnd(lines, catchLine);
            if (catchClose is null
                || !ResultFailure.IsMatch(string.Join('\n', lines[(catchLine + 1)..catchClose.Value])))
            {
                continue;
            }

            // A literal `return Result.X` in the try body is the syntactic
            // nested case; a `return <callee>(...)` one is in the ratchet.
            if (NestedResultReturn.IsMatch(tryBody))
            {
                continue;
            }

            if (exempt.Contains($"{rel}:{catchLine + 1}"))
            {
                continue;
            }

            yield return $"{rel}:{catchLine + 1} — catch(Exception) builds Result.Failure by hand; use Result.Try";
        }
    }

    /// <summary>Index of the line closing the block opened at or after <paramref name="start" />.</summary>
    private static int? FindBlockEnd(string[] lines, int start)
    {
        int open = -1;
        for (int i = start; i < lines.Length; i++)
        {
            if (lines[i].Contains('{'))
            {
                open = i;
                break;
            }

            if (lines[i].Contains(';'))
            {
                return null; // `try;` — not a block
            }
        }

        if (open < 0)
        {
            return null;
        }

        int depth = 0;
        for (int i = open; i < lines.Length; i++)
        {
            depth += lines[i].Count(ch => ch == '{') - lines[i].Count(ch => ch == '}');
            if (depth == 0 && i > open)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Index of the first <c>catch</c> line at or after <paramref name="from" />.</summary>
    private static int FindCatchLine(string[] lines, int from)
    {
        for (int i = from; i < Math.Min(from + 10, lines.Length); i++)
        {
            if (lines[i].TrimStart().StartsWith("catch", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
