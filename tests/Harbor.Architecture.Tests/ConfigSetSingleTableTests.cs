// ConfigSetSingleTableTests.cs — issue #709: `set` has ONE key table.
//
// WHY THIS FILE EXISTS
// --------------------
// `set <key> <value>` is implemented TWICE in this assembly, and for most of the
// repository's life the two copies answered the same question differently:
//
//   apps/Harbor.App.Cli/Commands/ConfigCommand.cs         — the slash command.
//       Backs BOTH entry points: `SlashCommandDispatcher` registers it for
//       `/config`, and `ConfigVerb.RunAsync` constructs the same class for
//       `harbor config`. One class, two surfaces.
//   apps/Harbor.App.Cli/Repl/Commands/ConfigCommand.cs     — the CellForge
//       palette's `/config` menu, registered by `ReplCommandCatalog.CreateDefault`.
//
// Both assigned through `HarborConfig`'s property setters, which are
// `_ = TrySet…(value)` — they parse, and on failure DISCARD. The first copy
// printed `✓ {key} = {value}` for the discarded value; the second had already
// worked out that `maxsteps`/`costlimit` must be refused and had given them the
// `✗ Invalid MaxSteps value: '…'` treatment, while `model`/`provider`/`agent`
// got the same `✓` the other one did. So the two `/config` surfaces disagreed
// about half the table, and neither of them was right about the other half.
//
// One table, in `Harbor.App.Cli/Configuration/ConfigValueSetter.cs`, decides
// what every key accepts and is the only place that says so. This gate keeps it
// that way: a second switch over config keys, or a second `TryParse` guard, in
// either command file fails the build.
//
// IT IS TEXTUAL, AND HERE IS WHY THAT IS THE RIGHT TRADE
// -------------------------------------------------------
// The defect is a call site's CORRECTNESS, not an API's existence. `TryParse` is
// too broad a symbol to ban (`BannedSymbols.txt` cannot say "no `int.TryParse`
// in a command file" without also banning it in the tests and the parser
// itself), and there is no way to express "this switch must delegate" as a
// symbol rule. Same trade-off, same conclusion as `ModelRefSingleParserTests`
// and `DefaultModelSingleSourceTests`.
//
// KNOWN LIMITATION — stated, not hidden
// ------------------------------------
// The scan skips LINES whose first non-whitespace characters are `//`, `/*` or
// `*`, so a key switch hidden in a block comment would be missed. That is
// bounded by construction: the positive half of rule 1 is asserted separately,
// and a commented-out switch is not a second IMPLEMENTATION. What a text scan
// cannot see is a *runtime* divergence, which is why the behavioural table in
// `tests/Harbor.App.Cli.Tests/ConfigSetValueReportingTests.cs` is the real
// proof and this file is the ratchet that stops the shape coming back.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #709: one table decides what <c>/config set</c> accepts, and both
///     command surfaces route through it.
/// </summary>
public sealed class ConfigSetSingleTableTests
{
    /// <summary>The slash command — also the CLI verb's implementation.</summary>
    private const string SlashCommandFile = "apps/Harbor.App.Cli/Commands/ConfigCommand.cs";

    /// <summary>The CellForge palette's <c>/config</c> menu.</summary>
    private const string PaletteCommandFile = "apps/Harbor.App.Cli/Repl/Commands/ConfigCommand.cs";

    /// <summary>The one table both of them must reach.</summary>
    private const string TableFile = "apps/Harbor.App.Cli/Configuration/ConfigValueSetter.cs";

    /// <summary>
    ///     A switch arm over a config key. Matched on the lower-case key names
    ///     the two surfaces actually expose, so a rename cannot slip past by
    ///     becoming <c>MaxSteps</c>.
    /// </summary>
    private static readonly Regex KeyArm = new(
        @"\bcase\s+""(?<key>model|provider|agent|tui|storage|maxsteps|costlimit)""\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A numeric guard written next to the key switch instead of in the table.</summary>
    private static readonly Regex InlineNumericParse = new(
        @"\b(?:int|decimal|double|long)\s*\.\s*(?:TryParse|Parse)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Delegation to the table: a real call, not a mention in prose.</summary>
    private static readonly Regex DelegatesToTable = new(
        @"\bConfigValueSetter\s*\.\s*(?:Decide|Keys)\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     A key offered by the palette's <c>set</c> SUBMENU:
    ///     <c>new("model", "Model", …)</c>.
    /// </summary>
    /// <remarks>
    ///     Scoped to the <c>keyItems</c> list, and NOT applied to the whole file.
    ///     The palette command builds TWO menus: the top-level <c>/config</c> frame
    ///     (<c>view</c>, <c>set</c>, <c>path</c> — verbs of the command, not config
    ///     keys) and the <c>config / set</c> submenu this rule is about. An
    ///     unscoped match reads all six and reports three phantom offenders, which
    ///     is what the first run of this guard did.
    /// </remarks>
    private static readonly Regex PaletteKeyOffer = new(
        @"\bnew\s*\(\s*""(?<key>[a-z]+)""\s*,",
        RegexOptions.Compiled);

    /// <summary>The list the <c>config / set</c> frame is built from.</summary>
    private const string SetSubmenuListDeclaration = "keyItems";

    // ── Rule 1, negative half: neither command file may hold its own table ──

    [Test]
    public async Task NeitherCommandFile_CarriesItsOwnKeyTable()
    {
        string root = RequireRepoRoot();
        var violations = new List<string>();

        foreach (string file in new[] { SlashCommandFile, PaletteCommandFile })
        {
            foreach ((int line, string text) in Matches(Read(root, file), KeyArm))
            {
                violations.Add($"{file}:{line}: `case \"{Match(text, KeyArm)}\"` — the key table lives in {TableFile}");
            }

            foreach ((int line, string text) in Matches(Read(root, file), InlineNumericParse))
            {
                violations.Add(
                    $"{file}:{line}: a numeric parse beside the key switch — the rule for what `maxsteps` / "
                    + $"`costlimit` accept belongs in {TableFile}, where the `else` that reports the refusal "
                    + "cannot be forgotten. This is the exact shape issue #709 filed: `if (int.TryParse(…)) c.MaxSteps = ms;` "
                    + "with no `else`, so a value nobody kept was reported as `✓ maxsteps = <value>`.");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "`set <key> <value>` is implemented in two places, and the two copies disagreed: the palette "
                + "refused `maxsteps`/`costlimit` in words while the slash command — which is also the CLI verb's "
                + "whole implementation — confirmed them, and both confirmed a discarded `model`. Two `/config` "
                + "surfaces answering one question differently is how the same bug got fixed once and stayed "
                + "broken on the other path. A second table here re-creates that. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Rule 1, positive half: both must actually reach the table ──────────

    [Test]
    public async Task BothCommandFiles_DelegateToTheTable()
    {
        string root = RequireRepoRoot();
        var silent = new List<string>();

        foreach (string file in new[] { SlashCommandFile, PaletteCommandFile })
        {
            if (CountDelegations(Read(root, file)) == 0)
            {
                silent.Add(file);
            }
        }

        await Assert.That(silent).IsEmpty()
            .Because(
                "A file that merely stopped containing a switch has not fixed anything — it has moved the rule "
                + "somewhere less visible, and only this assertion notices: " + string.Join(", ", silent)
                + " must call ConfigValueSetter, because that is where a refused value is refused.");
    }

    // ── Rule 2: the palette may not offer a key the table does not decide ──

    [Test]
    public async Task EveryPaletteKey_IsDecidedByTheTable()
    {
        string root = RequireRepoRoot();
        string table = Read(root, TableFile);

        var tableKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach ((_, string text) in Matches(table, KeyArm))
        {
            tableKeys.Add(Match(text, KeyArm).ToLowerInvariant());
        }

        var undecided = new List<string>();
        foreach ((int line, string text) in MatchesInSetSubmenu(Read(root, PaletteCommandFile), PaletteKeyOffer))
        {
            string key = Match(text, PaletteKeyOffer);
            if (!tableKeys.Contains(key))
            {
                undecided.Add($"{key} (offered at {PaletteCommandFile}:{line})");
            }
        }

        await Assert.That(tableKeys.Count).IsGreaterThan(0)
            .Because(
                "Non-vacuity: the table declares the keys it decides. Zero means the matcher stopped matching — "
                + "a `switch (key)` moved to an expression, say — and this rule became decoration.");

        await Assert.That(undecided).IsEmpty()
            .Because(
                "The palette's `set` submenu lists keys a user picks from. A key it offers that the table has no "
                + "arm for reaches `default:` and can only ever come back as `Unknown config key`, so the menu "
                + "advertises a setting that cannot be set: " + string.Join(", ", undecided));

        await Assert.That(SubmenuKeys(Read(root, PaletteCommandFile)).Count).IsGreaterThan(0)
            .Because(
                "Non-vacuity: the scoped walk has to find the submenu at all. An empty result would satisfy the "
                + "rule above by finding nothing — the same vacuous pass the whole file is written against.");
    }

    // ── Non-vacuity: the matcher must see the shape it was written for ─────

    [Test]
    public async Task Matcher_SeesThePreFixSpellings_AndIgnoresProse()
    {
        // Positive control: #709's own line, verbatim from the pre-fix file.
        const string PreFixArms = """
            case "maxsteps":
                if (int.TryParse(value, out int ms)) c.MaxSteps = ms;
                break;
            case "costlimit":
                if (decimal.TryParse(value, out decimal cl)) c.CostLimit = cl;
                break;
            """;

        await Assert.That(ScanFor(PreFixArms, KeyArm).Count).IsEqualTo(2)
            .Because("Both key arms of the pre-fix switch are the shape rule 1 exists to catch.");

        await Assert.That(ScanFor(PreFixArms, InlineNumericParse).Count).IsEqualTo(2)
            .Because(
                "The `TryParse` guard with no `else` is the second half of the same defect, and it gets its "
                + "own rule precisely because an arm that is a bare `case \"maxsteps\": break;` looks harmless.");

        await Assert.That(ScanFor("    if (int.TryParse(value, out int ms)) c.MaxSteps = ms;", InlineNumericParse).Count)
            .IsEqualTo(1);

        await Assert.That(ScanFor("/// <c>case \"model\"</c> is discussed here.", KeyArm).Count).IsEqualTo(0)
            .Because("A doc comment naming a key is prose, not an arm.");
        await Assert.That(ScanFor("// case \"provider\": c.Provider = value; break;", KeyArm).Count).IsEqualTo(0)
            .Because("A commented-out arm is history, not a second implementation.");
        await Assert.That(ScanFor("    case \"somethingelse\": break;  // a non-config switch", KeyArm).Count)
            .IsEqualTo(0)
            .Because("A switch over something that is not a config key is not this rule, or the gate is noise.");
        await Assert.That(ScanFor("    case \"MAXSTEPS\": break;", KeyArm).Count).IsEqualTo(1)
            .Because(
                "Case-insensitive on purpose: `set` lower-cases the key before dispatching, so a differently "
                + "cased arm is the same arm, and a rule that only matched one spelling would not hold it.");
        await Assert.That(ScanFor("    case \"maxsteps\": break;  // trailing comment", KeyArm).Count).IsEqualTo(1)
            .Because("A code line with a trailing comment is still a code line.");
    }

    [Test]
    public async Task DelegationMatcher_SeesACall_AndNotAMention()
    {
        await Assert.That(CountDelegationsIn(["var d = ConfigValueSetter.Decide(key, value);"])).IsEqualTo(1);
        await Assert.That(CountDelegationsIn(["/// ConfigValueSetter holds the key table."])).IsEqualTo(0)
            .Because("The XML docs name the type while explaining the rule; reading that as delegation disarms the positive half.");
        await Assert.That(CountDelegationsIn(["// see ConfigValueSetter.Decide"])).IsEqualTo(0);
    }

    // ── plumbing ───────────────────────────────────────────────────────────

    private static string RequireRepoRoot()
    {
        string? root = RepoPaths.RepoRoot;
        return root ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");
    }

    private static string Read(string root, string relative)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, relative));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>0-based line numbers holding a match, comment lines skipped.</summary>
    private static IReadOnlyList<int> ScanFor(string text, Regex regex)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var hits = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsCommentLine(lines[i]) && regex.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static IEnumerable<(int Line, string Text)> Matches(string text, Regex regex)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsCommentLine(lines[i]) && regex.IsMatch(lines[i]))
            {
                yield return (i + 1, lines[i]);
            }
        }
    }

    /// <summary>
    ///     The 1-based line numbers of the <c>keyItems</c> initializer — the list
    ///     the <c>config / set</c> frame is built from, and nothing else in the
    ///     file.
    /// </summary>
    /// <remarks>
    ///     Found by brace count from the declaration, not by a fixed line span, so
    ///     an item added above or below it does not silently fall out of the rule.
    ///     Braces are counted on the line as written; string literals in this
    ///     list are short and balanced, so a count that includes them is still the
    ///     count that ends the initializer.
    /// </remarks>
    private static IReadOnlyList<int> SetSubmenuLines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var span = new List<int>();
        bool inside = false;
        int depth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if (!inside)
            {
                if (!IsCommentLine(lines[i]) && lines[i].Contains(SetSubmenuListDeclaration))
                {
                    inside = true;
                    depth = 0;
                }

                continue;
            }

            span.Add(i + 1);
            depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
            if (depth <= 0)
            {
                break;
            }
        }

        return span;
    }

    private static IEnumerable<(int Line, string Text)> MatchesInSetSubmenu(string text, Regex regex)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (int line in SetSubmenuLines(text))
        {
            string body = lines[line - 1];
            if (!IsCommentLine(body) && regex.IsMatch(body))
            {
                yield return (line, body);
            }
        }
    }

    private static IReadOnlyList<string> SubmenuKeys(string text)
        => [.. MatchesInSetSubmenu(text, PaletteKeyOffer).Select(m => Match(m.Text, PaletteKeyOffer))];

    private static string Match(string text, Regex regex)
    {
        Match m = regex.Match(text);
        return m.Success ? m.Groups["key"].Value : string.Empty;
    }

    private static int CountDelegations(string text) => CountDelegationsIn(
        text.Replace("\r\n", "\n").Split('\n'));

    private static int CountDelegationsIn(IReadOnlyList<string> lines)
    {
        int count = 0;
        foreach (string line in lines)
        {
            if (!IsCommentLine(line) && DelegatesToTable.IsMatch(line))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith('*');
    }
}
