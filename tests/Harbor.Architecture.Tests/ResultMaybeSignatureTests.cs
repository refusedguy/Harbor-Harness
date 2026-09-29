// ResultMaybeSignatureTests.cs — guard for the ROP wave that replaced every
// `Result<T?>` in the non-test tree with `Result<Maybe<T>>`.
//
// WHY THIS EXISTS
//
// `Result<T?>` is a lie in a signature. It says "the value may be null" but it
// does NOT say the caller must handle it, and it cannot tell "the call succeeded
// and there is legitimately no value" apart from "the call failed" — both arrive
// as `IsSuccess == true` with a null `Value` if the author got it wrong, and
// only the author knows which. CSharpFunctionalExtensions 3.7.0 ships the honest
// shape for exactly this case: `Result<Maybe<T>>`, which states three things at
// once — the lookup can fail, the value can be absent, and if it failed the
// reason is right there. `src/Harbor.Lsp/LspServerSession.cs` already used it;
// it was the only place in the tree that did.
//
// The wave removed the last 17: 16 in `Harbor.Tools.Builtin` (all in Tools/Mcp —
// the two remote transports plus the OAuth config / discovery / loopback-redirect
// trio) and 1 in `Harbor.Storage.Jsonl`. `EnsureNotNull` had 0 call sites
// repo-wide against those 17 signatures, and `Maybe<` had 2 uses outside
// Harbor.Lsp.
//
// A shape that only one commit enforces is a shape that rots. This test is the
// enforcement: re-introduce `Result<string?>` anywhere under `src/` and CI goes
// red with the file and the line.
//
// SCOPE
//
// All of `src/`. `apps/` and `contrib/` are also clean today (verified by the
// same scan) but are not gated — they are composition roots and script bridges,
// where a `Result<T?>` is not the ROP perimeter's problem. Extend the scan there
// when one shows up; do not delete the rule.
//
// The scan is textual rather than reflective on purpose. A reflective version
// has to distinguish `Result<SessionId>` (a value object — correct, never null
// by construction) from `Result<string?>` (the smell), and the only way to do
// that is `NullabilityInfoContext` over every member of every type, including
// generic methods. That is a lot of machinery that can itself break. The
// textual form `Result<T?>` is exactly the banned shape, appears in one
// syntactic position, and cannot drift into a false negative.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Fails the build if a <c>Result&lt;T?&gt;</c> signature reappears anywhere
///     under <c>src/</c>. See the file header for the rationale and scope.
/// </summary>
public class ResultMaybeSignatureTests
{
    /// <summary>
    ///     Matches the banned form <c>Result&lt;T?&gt;</c> only.
    ///     <list type="bullet">
    ///     <item><c>Result&lt;JsonDocument?&gt;</c> — matches (the smell).</item>
    ///     <item><c>Result&lt;Maybe&lt;JsonDocument&gt;&gt;</c> — no match; <c>?&gt;</c> cannot
    ///     follow <c>Maybe</c> because the next character is <c>&lt;</c>.</item>
    ///     <item><c>Result.Success&lt;JsonDocument?&gt;</c> — no match; the text is
    ///     <c>Result.Success&lt;</c>, so <c>Result&lt;</c> never occurs.</item>
    ///     <item><c>Result&lt;Nullable&lt;int&gt;&gt;</c> — no match, and correct anyway
    ///     (<c>int?</c> is a value type and is a legitimate <c>Result</c> payload).</item>
    ///     </list>
    /// </summary>
    private static readonly Regex NullableResultValue =
        new(@"\bResult<[A-Za-z_][A-Za-z0-9_.]*\?>", RegexOptions.Compiled);

    /// <summary>
    ///     No <c>Result&lt;T?&gt;</c> anywhere under <c>src/</c>. On a hit, replace
    ///     it with <c>Result&lt;Maybe&lt;T&gt;&gt;</c>: the <c>null</c> cases become
    ///     <c>Maybe&lt;T&gt;.None</c>, the value cases become
    ///     <c>Maybe&lt;T&gt;.From(x)</c>, and any read of the value goes through
    ///     <c>.HasValue</c> / <c>.HasNoValue</c> — which is the point, because
    ///     those are the two states <c>Result&lt;T?&gt;</c> collapsed into one.
    ///     <para>
    ///     Unwrapping a <c>Maybe&lt;T&gt;</c> back to a nullable reference is
    ///     <c>m.HasValue ? m.Value : null</c> — the pattern
    ///     <c>src/Harbor.Lsp/LspServerSession.cs:158</c> already uses. Do
    ///     <b>not</b> reach for <c>.AsNullable()</c>: in 3.7.0 it is declared
    ///     <c>AsNullable&lt;T&gt;(ref Maybe&lt;T&gt;) where T : struct</c>, so it does
    ///     not apply to a reference-type <c>T</c> at all and the build rejects it
    ///     with CS0453. <c>Maybe&lt;T&gt;.Value</c> is safe because <c>HasValue</c>
    ///     carries <c>[MemberNotNullWhen]</c>, so no <c>!</c> is needed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Assert_NoResultOfNullableValueTypeUnderSrc()
    {
        var violations = new List<string>();
        int scanned = 0;

        foreach (string file in EnumerateSrcSourceFiles())
        {
            scanned++;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                // Strip line comments so a doc comment that merely *mentions* the
                // old shape (e.g. "was Result<string?>") does not trip the rule.
                // A "//" inside a string literal cannot produce a match for this
                // pattern, so the naive split is safe here.
                string code = lines[i];
                int comment = code.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    code = code[..comment];
                }

                if (NullableResultValue.IsMatch(code))
                {
                    violations.Add($"{RepoRelative(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        // Self-check: a guard that silently scans zero files is worse than no
        // guard, because it reads as a green light forever.
        await Assert.That(scanned).IsGreaterThan(100)
            .Because($"the Result<T?> guard must actually see src/ source files — it reported {scanned}, so RepoPaths.RepoRoot is probably null (not running from a checkout)");

        await Assert.That(violations).IsEmpty()
            .Because(
                $"Result<T?> is banned under src/: it hides \"absent\" inside \"succeeded\" and gives the caller no branch to write. Use Result<Maybe<T>> — Maybe<T>.None for the absent case, Maybe<T>.From(x) for a value, and read back with m.HasValue ? m.Value : null (NOT .AsNullable(), which is where T : struct in 3.7.0). Offenders:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     Every <c>.cs</c> file under the repository's <c>src/</c> directory,
    ///     excluding build output so a stale <c>obj/</c> copy is never counted.
    ///     Empty when not running from a checkout — the assertion above catches
    ///     that case rather than letting the test pass vacuously.
    /// </summary>
    private static IEnumerable<string> EnumerateSrcSourceFiles()
    {
        if (RepoPaths.RepoRoot is null)
        {
            yield break;
        }

        string src = Path.Combine(RepoPaths.RepoRoot, "src");
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

    /// <summary>Repo-relative path, so failure messages are stable and clickable.</summary>
    private static string RepoRelative(string absolutePath)
        => RepoPaths.RepoRoot is null
            ? absolutePath
            : Path.GetRelativePath(RepoPaths.RepoRoot, absolutePath);
}
