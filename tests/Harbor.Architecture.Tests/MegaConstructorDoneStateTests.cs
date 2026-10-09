// MegaConstructorDoneStateTests.cs — done-state guard for issue #486 findings 3–4.
//
// Finding 3: CellForgeReplRunner's primary constructor (24 parameters on dev:
// the issue says 22 — IPanelRegistry and IGitQuery landed after it was filed).
// Finding 4: SessionLifecycleService's constructor (10 parameters on dev: the
// issue says "11 (9 collaborators + logger)" — 9 + 1 is 10, the issue miscounted).
//
// Scope, stated so this gate cannot be misread: it names exactly these two
// types. ReplRunner (finding 1, 19 parameters) is deliberately NOT asserted
// here — that finding was closed by #776 on the retained-vs-composed
// distinction, kept by Constructor_RetainsEveryParameterItDeclares in
// ReplConstructorCompositionTests, and this file does not relitigate it.
// SlashCommandDispatcher placement (finding 2) is likewise NOT asserted here —
// ContainerOwnedType_IsConstructedInTheCompositionRootOnly already grades it.
//
// RED first: both tests fail on the pre-fix tree (24 and 10). The fix commits
// make them green without touching this file's assertions.

namespace Harbor.Architecture.Tests;

/// <summary>
///     Done-state counts for the two mega-constructors issue #486 still owes.
/// </summary>
public sealed class MegaConstructorDoneStateTests
{
    private const string CellForgeRunnerRelative = "apps/Harbor.App.Cli/Repl/CellForgeReplRunner.cs";
    private const string CellForgeRunnerType = "CellForgeReplRunner";

    private const string LifecycleRelative = "src/Harbor.Ui.Framework.Sessions/Sessions/SessionLifecycleService.cs";
    private const string LifecycleType = "SessionLifecycleService";

    [Test]
    public async Task CellForgeRunner_TakesAtMostEightParameters()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        string text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, CellForgeRunnerRelative));
        int count = CountParameters(text, "class " + CellForgeRunnerType);

        await Assert.That(count).IsGreaterThan(0)
            .Because(
                "Non-vacuity: the walk must recover the primary constructor of "
                + CellForgeRunnerType + ". A count of zero means the declaration "
                + "moved or was respelled and this gate is decorative.");

        await Assert.That(count).IsLessThanOrEqualTo(8)
            .Because(
                "Issue #486 finding 3: " + CellForgeRunnerType + " declares " + count
                + " constructor parameters. Group the container services into cohesive "
                + "parameter objects (the six CellForgeScreens members the root already "
                + "builds travel as the aggregate itself) and keep only the per-run "
                + "values beside them.");
    }

    [Test]
    public async Task SessionLifecycleService_TakesAtMostEightParameters()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        string text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, LifecycleRelative));
        int count = CountParameters(text, "public " + LifecycleType);

        await Assert.That(count).IsGreaterThan(0)
            .Because(
                "Non-vacuity: the walk must recover the constructor of "
                + LifecycleType + ". A count of zero means the declaration "
                + "moved or was respelled and this gate is decorative.");

        await Assert.That(count).IsLessThanOrEqualTo(8)
            .Because(
                "Issue #486 finding 4: " + LifecycleType + " declares " + count
                + " constructor parameters. Group the session collaborators behind "
                + "one scope object resolved through the container.");
    }

    /// <summary>
    ///     Number of top-level comma-separated entries in the first parenthesised
    ///     list following <paramref name="declaration" />. Angle/paren depth aware
    ///     so generic arguments do not split; comments stripped first so the
    ///     rationale comments inside the primary constructor do not desynchronise
    ///     the depth counter.
    /// </summary>
    private static int CountParameters(string text, string declaration)
    {
        string clean = StripComments(text);
        int at = clean.IndexOf(declaration, StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        int open = clean.IndexOf('(', at);
        if (open < 0)
        {
            return 0;
        }

        int depth = 0;
        int close = -1;
        for (int i = open; i < clean.Length; i++)
        {
            if (clean[i] == '(')
            {
                depth++;
            }
            else if (clean[i] == ')' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return 0;
        }

        string list = clean[(open + 1)..close];
        if (string.IsNullOrWhiteSpace(list))
        {
            return 0;
        }

        int entries = 1;
        int inner = 0;
        bool inString = false;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (inString)
            {
                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c is '<' or '(')
            {
                inner++;
            }
            else if (c is '>' or ')')
            {
                inner--;
            }
            else if (c == ',' && inner == 0)
            {
                entries++;
            }
        }

        return entries;
    }

    private static string StripComments(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    builder.Append(' ');
                    i++;
                }

                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                builder.Append("  ");
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    builder.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                builder.Append("  ");
                i = Math.Min(i + 2, text.Length);
                continue;
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }
}
