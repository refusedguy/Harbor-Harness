// SlashCommandContextScopeTests.cs — guard for the slash-command context bag
// (issue #483).
//
// THE DEFECT
// ----------
// `SlashCommandDispatcher` used to hand every one of its seventeen handlers the
// same per-call `CommandContext` record of SIXTEEN collaborators: the writer and
// the reader, the session, the agent, the agent registry, the provider registry,
// the config store, the auth store, the tool registry, the session store, the
// onboarding wizard, the permission service — and then four OPTIONAL tail
// members (the plugin-reload service, the renderer pipeline, the skill-refresh
// and skill-update delegates).
//
// Those four are not optional to the COMMAND — they are optional to the HOST,
// which is a different fact entirely (a MINIMAL host never registers the plugin
// runtime), and that host-shape decision was leaking into the parameter every
// command is given.
//
// WHY THE RULE IS "NOT A TYPE THE DISPATCHER ALREADY HOLDS"
// -------------------------------------------------------
// Splitting the bag into `ICommandOutput` / `ISessionScope` / `IAgentScope`
// groups does NOT fix this: hand all three groups to all seventeen commands and
// `/help` can still reach `ctx.AgentScope.Permissions`. The service locator
// survives, one level down.
//
// What removes reach is the one fact the constructor already proves. Eight of
// the sixteen members are NOT per-call state at all — they are the dispatcher's
// OWN lifetime fields (`_tools`, `_sessions`, `_wizard`, `_permissions`,
// `_pluginReload`, `_rendererPipeline`, `_skillRefresh`, `_skillUpdate`),
// assigned before `_byName = BuildRegistry()` runs. The per-call `CommandContext`
// re-boxed all eight on every keystroke and handed them to every command, so a
// command could reach a service the dispatcher had already closed over and that
// the command had no business naming.
//
// So the rule is derived, not remembered: a member of the per-call bag may not
// be a type the dispatcher holds as a field. Moving such a member out is the
// whole fix — the registration group closes over the field instead, and the
// compiler then forbids `/help` from naming `PluginReloadService` at all.
//
// WHY A SOURCE SCAN
// -----------------
// "Which types does this one record declaration carry, and which types does this
// one class hold as fields" is a textual fact about a single composition-root
// file, and both sides have to be read from the SAME text or the rule compares a
// declaration against a stale hand-list. The scan is anchored on this one file
// rather than a tree, because there is exactly one such record.
//
// NON-VACUITY
// -----------
// `Scanner_FlagsTheReBoxedMember` is the positive control — issue #483's
// declaration verbatim, where eight members are dispatcher-owned — paired with
// the fixed spelling and with a case that pins the normalisation: the bag's
// `Reader` is `Func<string, Task<string>>` while the dispatcher's
// `_skillRefresh` is `Func<IReadOnlyList<SkillFreshnessEntry>>`. Collapsing both
// to the bare name `Func` would flag the one member that is legitimately
// per-call, so the matcher compares full generic shape.
// `ScopeMarkerFile_StillExists` keeps the gate from passing over nothing.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: a slash command's context may not carry a collaborator
///     the dispatcher already owns, because that collaborator is not per-call state
///     and re-boxing it into every command's parameter is what made the bag a
///     service locator.
/// </summary>
public sealed class SlashCommandContextScopeTests
{
    /// <summary>
    ///     The composition root that declares the bag. One file, because the defect
    ///     is one declaration in it.
    /// </summary>
    private const string DispatcherRelative = "apps/Harbor.App.Cli/Repl/SlashCommandDispatcher.cs";

    /// <summary>The bag this gate is about.</summary>
    private const string BagName = "CommandContext";

    /// <summary>
    ///     A <c>private readonly</c> field, capturing its declared type. Matches
    ///     the fully-qualified spellings the dispatcher uses for its host services
    ///     (<c>Harbor.Hosting.PluginReloadService? _pluginReload</c>).
    /// </summary>
    private static readonly Regex ReadonlyField = new(
        @"private\s+readonly\s+(?<type>[^;=\r\n]+?)\s+_(?<name>[A-Za-z_]\w*)\s*(?:=[^;]*)?;",
        RegexOptions.Compiled);

    /// <summary>
    ///     The bag's parameter list. Deliberately matched up to the opening paren
    ///     only: the body is read by <see cref="ReadBagMembers" />, which needs the
    ///     raw text to split nested generics correctly.
    /// </summary>
    private static readonly Regex BagDeclaration = new(
        $@"record\s+{BagName}\s*\(",
        RegexOptions.Compiled);

    // ── The rule ────────────────────────────────────────────────────────────

    [Test]
    public async Task CommandContext_CarriesNoDispatcherOwnedService()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        string full = Path.Combine(RepoPaths.RepoRoot, DispatcherRelative);
        string text = File.ReadAllText(full);

        IReadOnlyList<string> fields = ReadDispatcherFieldTypes(text);
        IReadOnlyList<BagMember> members = ReadBagMembers(text, BagName);

        await Assert.That(fields.Count).IsGreaterThan(4)
            .Because(
                "Non-vacuity, and the premise of the whole rule: the dispatcher holds a set of lifetime "
                + "services as fields, and the bag is forbidden from re-boxing them. " + fields.Count
                + " private readonly fields were read from " + DispatcherRelative
                + " — if that walk stopped matching (a renamed modifier, a moved file) every assertion below "
                + "is satisfied by an empty set and the gate is decorative. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(members.Count).IsGreaterThan(0)
            .Because(
                "Non-vacuity: " + BagName + " was not found in " + DispatcherRelative
                + ". A bag that cannot be read is a bag that cannot be policed, and the gate would pass "
                + "over the very declaration it was written for.");

        var offenders = new List<string>();
        foreach (BagMember member in members)
        {
            if (fields.Contains(member.NormalisedType, StringComparer.Ordinal))
            {
                offenders.Add(
                    $"{DispatcherRelative}: `{BagName}.{member.Name}` is a `{member.Type}` — a type the "
                    + "dispatcher already holds as a field, so it is not per-call state. Close over the "
                    + "field in the registration group that needs it instead of re-boxing it into the "
                    + "parameter every command is given.");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because(
                "A bag member that the dispatcher already owns is reach the command never needed: it is "
                + "in the parameter because it was convenient, not because the command named it. This is "
                + "the difference between a context and a service locator, and it is invisible at the call "
                + "site — a handler that takes the bag can reach any of its members without saying so. "
                + "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ── Self-check: the scanner must not run vacuously ─────────────────────

    [Test]
    public async Task Scanner_FlagsTheReBoxedMember()
    {
        // Positive control: issue #483's declaration verbatim, trimmed. All eight
        // of the trailing members are types the dispatcher holds as fields, and
        // every one of them must be flagged — a matcher that stops at the first
        // hit would under-report and the ratchet would be worthless.
        const string Reboxed = """
            public sealed record CommandContext(
                Action<string> Writer,
                Func<string, Task<string>>? Reader,
                Session Session,
                IAgent Agent,
                IAgentRegistry AgentRegistry,
                IProviderRegistry Providers,
                IConfigStore ConfigStore,
                AuthStore AuthStore,
                IToolRegistry ToolRegistry,
                ISessionStore SessionStore,
                OnboardingWizard Wizard,
                IPermissionService Permissions,
                Harbor.Hosting.PluginReloadService? PluginReload = null,
                Harbor.Hosting.Rendering.IRendererPipeline? RendererPipeline = null,
                Func<IReadOnlyList<SkillFreshnessEntry>>? SkillRefresh = null,
                Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? SkillUpdate = null);
            """;

        const string Fields = """
            private readonly IToolRegistry _tools;
            private readonly ISessionStore _sessions;
            private readonly OnboardingWizard _wizard;
            private readonly IPermissionService _permissions;
            private readonly Harbor.Hosting.PluginReloadService? _pluginReload;
            private readonly Harbor.Hosting.Rendering.IRendererPipeline? _rendererPipeline;
            private readonly Func<IReadOnlyList<SkillFreshnessEntry>>? _skillRefresh;
            private readonly Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? _skillUpdate;
            """;

        // Negative control: the fixed spelling. Every member is per-call state
        // that arrives as a HandleCoreAsync argument, so nothing is re-boxed.
        const string PerCallOnly = """
            public sealed record CommandContext(
                Action<string> Writer,
                Func<string, Task<string>>? Reader,
                Session Session,
                IAgent Agent,
                IAgentRegistry AgentRegistry,
                IProviderRegistry Providers,
                IConfigStore ConfigStore,
                AuthStore AuthStore);
            """;

        // The case a lazy normalisation gets wrong, and the reason the matcher
        // compares full generic shape instead of the bare name. `Reader` is
        // per-call and must NOT be flagged even though the dispatcher also holds
        // two `Func<...>` fields — collapsing both sides to `Func` would flag
        // every delegate in the bag and make the rule cry wolf on correct code,
        // which is how a gate gets switched off.
        const string DelegateIsNotAFuncCollision = """
            public sealed record CommandContext(
                Func<string, Task<string>>? Reader);
            """;

        await Assert.That(Flagged(Reboxed, Fields).Count()).IsEqualTo(8)
            .Because(
                "The positive control for the rule, verbatim from issue #483: all eight dispatcher-owned "
                + "services were members of the per-call bag. A miss means the matcher no longer recognises "
                + "the shape and the bag can silently grow back to a service locator.");

        await Assert.That(Flagged(PerCallOnly, Fields).Count()).IsEqualTo(0)
            .Because(
                "The fixed spelling must stay clean. Every member is per-call state that arrives as a "
                + "HandleCoreAsync argument, so flagging it would make the gate fire on the fix it asked for.");

        await Assert.That(Flagged(DelegateIsNotAFuncCollision, Fields).Count()).IsEqualTo(0)
            .Because(
                "Generic shape is load-bearing: the bag's `Reader` is `Func<string, Task<string>>` and the "
                + "dispatcher's `_skillRefresh` is `Func<IReadOnlyList<SkillFreshnessEntry>>`. Matching on "
                + "the bare name `Func` would flag a member that is genuinely per-call, and a gate that "
                + "fires on correct code is a gate that gets deleted.");
    }

    [Test]
    public async Task ScopeMarkerFile_StillExists()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        await Assert.That(File.Exists(Path.Combine(RepoPaths.RepoRoot, DispatcherRelative))).IsTrue()
            .Because(
                "The guard's perimeter moved. " + DispatcherRelative + " is where the bag is declared; if it "
                + "is renamed or moved, this gate is scanning a declaration it no longer describes and passes "
                + "vacuously. Update DispatcherRelative in the same commit that moves the file.");
    }

    // ── scanning ────────────────────────────────────────────────────────────

    /// <summary>One member of the bag, with its type normalised for comparison.</summary>
    private readonly record struct BagMember(string Name, string Type, string NormalisedType);

    /// <summary>
    ///     Runs the rule over two snippets: the bag's declaration and the
    ///     dispatcher's field list. Returns the offending member names.
    /// </summary>
    private static IReadOnlyList<string> Flagged(string source, string fieldSource)
    {
        IReadOnlyList<string> fields = ReadDispatcherFieldTypes(fieldSource);
        return
        [
            .. ReadBagMembers(source, BagName)
                .Where(m => fields.Contains(m.NormalisedType, StringComparer.Ordinal))
                .Select(m => m.Name)
        ];
    }

    /// <summary>
    ///     Every type the dispatcher holds as a <c>private readonly</c> field,
    ///     normalised the same way a bag member's type is. Ordering is stable so
    ///     failure messages do not shuffle between runs.
    /// </summary>
    private static IReadOnlyList<string> ReadDispatcherFieldTypes(string text)
    {
        var types = new List<string>();
        foreach (Match match in ReadonlyField.Matches(StripCommentsAndLiterals(text)))
        {
            string type = NormaliseType(match.Groups["type"].Value);
            if (type.Length > 0)
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>
    ///     The bag's members, in declaration order. The parameter list is read from
    ///     the RAW text (literals intact) because a default value may contain a
    ///     comma — <c>= null</c> today, but the splitter must not depend on that.
    /// </summary>
    private static IReadOnlyList<BagMember> ReadBagMembers(string text, string bagName)
    {
        var members = new List<BagMember>();
        Match declaration = BagDeclaration.Match(text);
        if (!declaration.Success)
        {
            return members;
        }

        int open = text.IndexOf('(', declaration.Index);
        int close = MatchParen(text, open);
        if (close < 0)
        {
            return members;
        }

        foreach (string parameter in SplitTopLevel(text[(open + 1)..close]))
        {
            // Drop any default value, at angle/paren depth 0: `= null` must not
            // be mistaken for part of the type. TrimEnd is load-bearing, not
            // cosmetic: cutting at `=` leaves the space in front of it, and
            // LastSpaceAtDepthZero would then pick THAT trailing space as the
            // type/name boundary — yielding an empty name and silently dropping
            // every member that has a default. All four host-shape members have
            // one, so the first cut of this scanner missed exactly the members
            // the rule is about.
            string head = CutAtDepthZero(parameter, '=').TrimEnd();
            if (string.IsNullOrWhiteSpace(head))
            {
                continue;
            }

            int split = LastSpaceAtDepthZero(head);
            if (split <= 0)
            {
                continue;
            }

            string type = head[..split].Trim();
            string name = head[(split + 1)..].Trim();
            if (type.Length == 0 || name.Length == 0)
            {
                continue;
            }

            members.Add(new BagMember(name, type, NormaliseType(type)));
        }

        return members;
    }

    /// <summary>
    ///     Canonical form for comparing two type spellings: namespace qualifiers
    ///     removed at every depth, nullability dropped, whitespace dropped. Generic
    ///     ARGUMENTS are deliberately kept — see the delegate control in
    ///     <see cref="Scanner_FlagsTheReBoxedMember" />.
    /// </summary>
    private static string NormaliseType(string type)
    {
        var builder = new StringBuilder(type.Length);
        int depth = 0;

        for (int i = 0; i < type.Length; i++)
        {
            char c = type[i];

            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }

            if (char.IsWhiteSpace(c) || c == '?')
            {
                continue;
            }

            // A namespace qualifier is an identifier followed by a dot, at any
            // depth. Dropping the qualifier keeps the type name and keeps inner
            // generic arguments, which is what the comparison needs.
            if (c == '.' && depth >= 0 && builder.Length > 0 && IsIdentifierChar(builder[^1]))
            {
                int end = builder.Length;
                while (end > 0 && IsIdentifierChar(builder[end - 1]))
                {
                    end--;
                }

                builder.Remove(end, builder.Length - end);
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '>';

    /// <summary>Index of the <c>)</c> closing the <c>(</c> at <paramref name="open" />, or -1.</summary>
    private static int MatchParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Splits a parameter list on commas that are not inside <c>&lt;&gt;</c> or
    ///     <c>()</c> — a bare split would tear
    ///     <c>Func&lt;IReadOnlyList&lt;string&gt;, Task&lt;…&gt;&gt;</c> in half.
    /// </summary>
    private static IReadOnlyList<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        foreach (char c in list)
        {
            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>Everything before the first <paramref name="target" /> at depth 0, or the whole text.</summary>
    private static string CutAtDepthZero(string text, char target)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }
            else if (c == target && depth == 0)
            {
                return text[..i];
            }
        }

        return text;
    }

    /// <summary>
    ///     Index of the last space at angle/paren depth 0 — the boundary between a
    ///     parameter's type and its name. Spaces INSIDE a generic argument list
    ///     (<c>Func&lt;A, B&gt;</c>) are at depth 1 and must not win.
    /// </summary>
    private static int LastSpaceAtDepthZero(string text)
    {
        int depth = 0;
        int found = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(')
            {
                depth++;
            }
            else if (c is '>' or ')')
            {
                depth--;
            }
            else if (c == ' ' && depth == 0)
            {
                found = i;
            }
        }

        return found;
    }

    /// <summary>
    ///     Blanks comments, preserving line structure, so a field or a bag quoted in
    ///     prose (including this file's own header, once it lives in the same tree)
    ///     is not read as a declaration.
    /// </summary>
    private static string StripCommentsAndLiterals(string text)
    {
        var builder = new StringBuilder(text.Length);
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

            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                builder.Append(' ');
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    builder.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                i = Math.Min(i + 1, text.Length);
                continue;
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }
}
