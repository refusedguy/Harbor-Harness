// ResultFailureConversionTests.cs — guard for the `ConvertFailure` wave.
//
// WHY THIS FILE EXISTS
// --------------------
// CSharpFunctionalExtensions 3.7.0 ships `ConvertFailure` on `Result`,
// `Result<T>`, `Result<T,E>` and `UnitResult<E>`:
//
//     public Result<K> ConvertFailure<K>()   // Result<T>  -> Result<K>
//     public Result   ConvertFailure()       // Result<T>  -> Result
//
// The repo had ZERO uses of it and 64 hand-rolled copies of the same operation,
// all of the shape `Result.Failure[<K>](<otherResult>.Error)`. That is a second
// implementation of a member the library already provides, written out longhand
// at every call site, and the drift risk is real: `ConvertFailure` THROWS
// `InvalidOperationException` on a success, so the guard that makes it safe
// (`if (x.IsFailure) return x.ConvertFailure<K>();`) is load-bearing. Delete the
// guard by accident and the site stops being a re-type and becomes a crash.
//
// The wave converted all 64. This file stops the 65th from landing.
//
// SCOPE
// -----
// All of `src/` and all of `apps/` — exactly the trees the wave touched.
// `contrib/` is not gated: it holds its own copy of the pattern behind the
// scripting bridge, and widening this gate would go red on a tree this wave
// does not change. Extend it there when a `contrib/` site is converted.
//
// WHY A TEXT SCAN AND NOT A REFLECTIVE TEST
// -----------------------------------------
// `ConvertFailure` is an instance method on a struct; nothing about the compiled
// assembly records which of two spellings produced a given `Result`. The banned
// form is also a single syntactic shape, so the text is the honest place to look
// — same trade-off, and the same conclusion, as ResultMaybeSignatureTests.
//
// COMMENT STRIPPING
// -----------------
// Only the code before the first `//` is matched, so this file — and any doc
// comment — may name the old shape without failing the build. A `//` inside a
// string literal cannot produce a match for this pattern, so the naive split is
// safe here.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: a failure is re-typed with <c>ConvertFailure</c>, never
///     rebuilt by hand, and the exemptions are documented rather than silent.
/// </summary>
public class ResultFailureConversionTests
{
    /// <summary>The trees the ConvertFailure wave converted.</summary>
    private static readonly string[] GuardedTrees = ["src", "apps"];

    /// <summary>
    ///     The hand-rolled re-type: <c>Result.Failure(x.Error)</c> and
    ///     <c>Result.Failure&lt;K&gt;(x.Error)</c>.
    ///     <list type="bullet">
    ///     <item>The type argument may nest one level, because
    ///     <c>Result.Failure&lt;IReadOnlyList&lt;AgentMessage&gt;&gt;(x.Error)</c> and
    ///     <c>Result.Failure&lt;Maybe&lt;JsonDocument&gt;&gt;(x.Error)</c> are both real
    ///     sites in this repo — a <c>[^&gt;]+</c> pattern cannot see them.</item>
    ///     <item>The leading <c>\b</c> is load-bearing:
    ///     <c>PluginCompilationResult.Failure(result.Error, …)</c> contains the
    ///     substring <c>Result.Failure</c> but has no word boundary before it, and
    ///     is a different type with a different factory.</item>
    ///     <item>Requiring the literal <c>.Error</c> after the receiver's dot is
    ///     what keeps <c>SessionStoreErrors.SessionNotFound(id)</c> out.</item>
    ///     </list>
    /// </summary>
    private static readonly Regex HandRolledFailureConversion = new(
        @"\bResult\.Failure(?:<(?:[^<>]|<[^<>]*>)*>)?\(\s*[A-Za-z_][A-Za-z0-9_]*\??\.Error\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Files allowed to keep the hand-rolled form, each with the reason the
    ///     receiver it still contains is not a <c>Result</c> at all. Adding an
    ///     entry is a decision, not an oversight — the reason is printed in the
    ///     failure message. The list is keyed per file, so a file with a mix of
    ///     convertible and non-<c>Result</c> sites says which is which.
    /// </summary>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Tui.CellForge/Chat/Widgets/JsonThemeLoader.cs"] =
            "The receiver is ThemeParseResult (src/Harbor.DesignSystem/DesignSystem/ThemeJson.cs:11) — "
            + "a hand-rolled `sealed record` with its own IsSuccess/Error/Theme, not a Result<T>. "
            + "ConvertFailure is an instance member of the Result family and does not exist on it, so "
            + "there is nothing to call. Making ThemeParseResult a Result<HarborTheme> would remove the "
            + "duplication at the source; that is a type change to a design-system contract, not this wave.",
        ["src/Harbor.Plugins.Hosting/PluginHost.cs"] =
            "One of the three sites here reads a CompilationResult, not a Result: "
            + "`IPluginCompiler.CompileAsync` returns `Task<CompilationResult>`, a hand-rolled "
            + "`readonly record struct` (src/Harbor.Plugins.Abstractions/IPluginCompiler.cs:42) with its own "
            + "IsSuccess/Error, so ConvertFailure does not exist on it and CS1061 rejects the call. "
            + "The other two sites in this file DO convert (Instantiate returns "
            + "Result<IReadOnlyList<LoadedPlugin>>, Register returns Result) — the allow-list is per "
            + "file, so this entry exempts the file's single non-Result site and nothing else."
    };

    [Test]
    public async Task GuardedTrees_DoNotHandRollFailureConversion()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles())
        {
            if (!HandRolledFailureConversion.IsMatch(text))
            {
                continue;
            }

            string relative = Relative(file);
            if (Exemptions.TryGetValue(relative, out string? reason))
            {
                _ = reason;
                continue;
            }

            violations.Add($"{relative}:{line} — hand-rolled failure re-type: {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "Re-typing a failure is Result<T>.ConvertFailure<K>() (or ConvertFailure() to drop the value), "
                + "not `Result.Failure<K>(other.Error)`. The member throws InvalidOperationException on a "
                + "success, so the `if (x.IsFailure)` guard in front of it must stay — that guard is also "
                + "what keeps the next line's `x.Value` legal. If the receiver is not a Result at all, add "
                + "the file to Exemptions with the reason. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public async Task Exemptions_AreStillUsed()
    {
        // Guards the guard: an exemption whose pattern has since been fixed is
        // dead weight that hides the rule it was protecting.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The exemption check walks the working tree; without a repository root it proves nothing.");

        if (root is null)
        {
            return;
        }

        var stale = new List<string>();

        foreach (string relative in Exemptions.Keys)
        {
            string absolute = Path.Combine(root, relative);
            if (!File.Exists(absolute) || !ScanFile(absolute).Any(hit => HandRolledFailureConversion.IsMatch(hit.Text)))
            {
                stale.Add($"{relative} — ConvertFailure exemption no longer matches anything");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "An exemption that no longer matches is silently dead: it would let the pattern come back "
                + "with nobody watching. Delete it, or update the reason to describe the new shape.");
    }

    [Test]
    public async Task Scanner_ReadsTheGuardedTreesAndIsNotVacuous()
    {
        // Guards the scanner itself. Four independent ways this test could pass
        // while checking nothing, so all four are asserted.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "The failure-conversion guard walks the working tree; without a repository root (no "
                + "Harbor.slnx above AppContext.BaseDirectory) every rule below would silently pass. That "
                + "would make the guard untestable in exactly the environment where it matters.");

        if (root is null)
        {
            return;
        }

        // 1. It reads a plausible number of files.
        int files = GuardedTrees
            .SelectMany(tree => Directory.GetFiles(Path.Combine(root, tree), "*.cs", SearchOption.AllDirectories))
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        await Assert.That(files).IsGreaterThan(900)
            .Because($"src/ + apps/ hold ~950 source files; the scanner saw {files}.");

        // 2. Comment stripping is real. Asserted two-sided on purpose: the whole
        //    comment must match the pattern (so the sample is not vacuous) and the
        //    truncated code the scanner actually sees must not. A one-sided
        //    "commentHits == 0" over the tree would pass for the wrong reason —
        //    ScanFile strips `//` first, so such a count is 0 by construction and
        //    proves nothing at all.
        const string trailingCommentLine =
            "            return resolved; // was: Result.Failure<Session>(resolved.Error);";
        int cut = trailingCommentLine.IndexOf("//", StringComparison.Ordinal);
        await Assert.That(cut).IsGreaterThan(0)
            .Because("The sample must have code in front of its '//', or the truncation below removes nothing.");
        await Assert.That(HandRolledFailureConversion.IsMatch(trailingCommentLine)).IsTrue()
            .Because("The sample must really contain the banned shape, or this check would be vacuous.");
        await Assert.That(HandRolledFailureConversion.IsMatch(trailingCommentLine[..cut])).IsFalse()
            .Because("ScanFile truncates each line at its first '//', so a comment naming the old shape cannot fail the build.");

        // 3. The pattern still matches the pre-wave spelling — a regex that stopped
        //    matching would make test #1 vacuously green forever.
        foreach (string sample in new[]
                 {
                     "            return Result.Failure<Session>(resolved.Error);",
                     "            return Result.Failure(resolved.Error);",
                     "            return Task.FromResult(Result.Failure<int>(resolved.Error));",
                     "            return Result.Failure<IReadOnlyList<CompiledPlugin>>(result.Error);",
                     "            return Result.Failure<Maybe<LspLocation>>(built.Error);"
                 })
        {
            await Assert.That(HandRolledFailureConversion.IsMatch(sample)).IsTrue()
                .Because($"The guard's pattern must still match the pre-wave spelling: {sample.Trim()}");
        }

        // 4. And it does NOT match the converted spelling, or the post-wave line
        //    would fail its own guard.
        foreach (string sample in new[]
                 {
                     "            return resolved.ConvertFailure<Session>();",
                     "            return resolved.ConvertFailure();",
                     "            return Task.FromResult(resolved.ConvertFailure());",
                     "            return result.ConvertFailure<IReadOnlyList<CompiledPlugin>>();",
                     "            return built.ConvertFailure<Maybe<LspLocation>>();",
                     "            return resolved.ConvertFailure<IReadOnlyList<AgentMessage>>();",
                     "        : Result.Failure<HarborTheme>($\"theme load failed: {ex.Message}\");",
                     "            return Result.Failure<Session>(SessionStoreErrors.SessionNotFound(id));"
                 })
        {
            await Assert.That(HandRolledFailureConversion.IsMatch(sample)).IsFalse()
                .Because($"The guard's pattern must not match a converted or unrelated line: {sample.Trim()}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Every code-only line of the guarded trees, with build output skipped.</summary>
    private static IEnumerable<(string File, int Line, string Text)> ScanGuardedFiles()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            yield break;
        }

        foreach (string tree in GuardedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach ((int line, string text) in ScanFile(file))
                {
                    yield return (file, line, text);
                }
            }
        }
    }

    /// <summary>
    ///     One file, one-based line numbers. Each line is truncated at its first
    ///     <c>//</c> so a comment cannot trip the rule; a whole-line comment
    ///     therefore yields an empty string and can never match.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> ScanFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            int comment = text.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                text = text[..comment];
            }

            if (text.Trim().Length == 0)
            {
                continue;
            }

            yield return (i + 1, text);
        }
    }

    /// <summary>Repo-relative, forward-slashed path for stable failure messages.</summary>
    private static string Relative(string absolutePath) =>
        (RepoPaths.RepoRoot is null ? absolutePath : Path.GetRelativePath(RepoPaths.RepoRoot, absolutePath))
        .Replace('\\', '/');
}
