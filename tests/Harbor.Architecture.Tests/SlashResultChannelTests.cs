// SlashResultChannelTests.cs — guard for the slash-command `Result` channel
// (issue #603).
//
// THE DEFECT
// ----------
// `SlashCommandDispatcher.SlashCommandRegistration.Execute` is
// `Func<CommandContext, IReadOnlyList<string>, Task<Result>>`, and its sole
// consumer was
//
//     var result = await reg.Execute(ctx, args);   // :192  assigned…
//     return SlashCommandOutcome.Continue;          // :194  …never read
//
// `SlashCommandOutcome(bool ShouldQuit, int ExitCode)` has no error member, and
// all three production entry points (`ReplRunner` x2, `PromptPipeline` via
// `LegacySlashRunner`) read only `ShouldQuit`/`ExitCode`. So the `Result` could
// not carry a failure anywhere. That is not a style complaint — a `Result` whose
// value is dropped is a contract that PROMISES the failure is handled somewhere
// and then silently drops it, and the handlers that relied on it looked correct
// while telling the user nothing.
//
// WHY A SOURCE SCAN, AND WHY IT IS SCOPED TO apps/Harbor.App.Cli
// --------------------------------------------------------------
// The defect is "a value nobody reads" and "a failure branch that does not
// exist" — neither is expressible as a symbol ban, and neither is visible in
// the compiled type graph (`Task<Result>` is perfectly legal on its own). So
// this is a source-text gate, following the mechanism this project already
// established in `ResultTryAdoptionTests` and `MapErrorFailureShapeTests`.
//
// The tree is `apps/Harbor.App.Cli`, not `src/`: that is the composition root
// where the slash-dispatch closure lives, and it is where the next copy of this
// shape will be written. The scan reaches the whole tree rather than a
// hand-picked file list precisely so a NEW command file is covered without
// anyone remembering to extend the list.
//
// THE TWO RULES
// -------------
//
//   A. A local bound from an `await` must be read before the end of the block
//      containing it. Counted over the enclosing block's own text, falling back
//      to the whole file when the block cannot be identified — the fallback
//      under-reports, and under-reporting is the right direction for a ratchet.
//      Scoping to the block is what makes the rule sharp: a file-global count
//      saw the name `result` elsewhere in SlashCommandDispatcher.cs (`/sessions`
//      declares one too) and so did NOT flag the very discard this gate exists
//      for. Block-scoped, it flags exactly one site in 1126 scanned files.
//
//   B. `if (x.IsSuccess)` must have a failure arm — `else`, `if (x.IsFailure)`,
//      `!x.IsSuccess`, or code after the block that INSPECTS `x`. `/tree` and
//      `/fork` in the same file already do this; `/sessions` did not, and
//      answered an unreadable store with silence. A bare `return x;` after the
//      block is NOT a failure arm: it hands the value on without looking at it,
//      which is the swallow this rule exists to catch.
//
//   C. An `ExecuteAsync` call may not be handed a hard-coded EMPTY argument
//      list (issue #650). This is the sibling of A and B and closes the same
//      loop from the other side: A and B are about a value produced by a
//      handler being dropped, C is about an input a handler was given never
//      arriving. The five slash registrations in SlashCommandDispatcher.cs bound
//      their arguments parameter as `_` and then called
//      `ExecuteAsync(Array.Empty<string>(), …)`, so `/config set model gpt-4`
//      took the no-args branch and dumped the configuration with the OLD value
//      still in it. The rule is one regular expression because the shape is
//      textual and unambiguous: a delegating registration is the only thing in
//      this tree that calls `ExecuteAsync`, and every one of those calls passes
//      the arguments it was given on the second line of the lambda.
//
// A source scan cannot see a callee's return type, so a write-only local bound
// from `await SomeIntMethod()` matches rule A too. That is a real (harmless)
// dead assignment rather than a false positive, and the alternative — teaching
// the scan a callee-type table that rots on every rename — is worse. The
// KnownExemptSites ratchet below is where such a site is declared.
//
// NON-VACUITY
// -----------
// A guard that cannot fire is worse than none, because it is believed. Four
// tests close that: `Scanner_FlagsTheDiscardedResult` is the positive control
// for rules A and B (the exact `:192` shape must be flagged) alongside three
// negative controls (the fixed spelling, and a read that lives inside an
// interpolation hole — the case a naive "blank out string literals first" scan
// gets wrong, because blanking the hole also deletes the only read), and
// `Scanner_FlagsTheDiscardedArguments` is the positive control for rule C
// (issue #650's `ExecuteAsync(Array.Empty<string>(), …)`) paired with the fixed
// spelling that forwards the arguments it was given.
// `AllowList_EveryEntryStillMatches`
// then fails when an exemption stops matching, so a stale entry cannot sit there
// letting the shape back in.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard: a <c>Result</c> a slash command produced must go
///     somewhere, and a failure branch must exist for the ones that can fail.
/// </summary>
public sealed class SlashResultChannelTests
{
    /// <summary>
    ///     The tree this gate governs. A composition root, and the only one that
    ///     hosts the slash-dispatch closure.
    /// </summary>
    private const string GuardedTree = "apps/Harbor.App.Cli";

    /// <summary>
    ///     Files whose existence proves the scan still covers the perimeter it
    ///     was written for. If these move, the scan silently covers nothing and
    ///     the gate passes vacuously.
    /// </summary>
    private static readonly string[] ScopeMarkerFiles =
    [
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "SlashCommandDispatcher.cs"),
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "SlashCommandOutcome.cs"),
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "LegacySlashRunner.cs"),
        Path.Combine("apps", "Harbor.App.Cli", "Commands", "AuthCommand.cs"),
    ];

    /// <summary>
    ///     A local declaration initialised from an <c>await</c>. The type is not
    ///     checked (a source scan cannot see a callee's return type), which is
    ///     what rule A's ratchet is for.
    /// </summary>
    private static readonly Regex AwaitedLocalDeclaration = new(
        @"^\s*(?:var|[A-Za-z_][\w<>,\.\?\[\] ]*?)\s+(?<name>[a-z][A-Za-z0-9_]*)\s*=\s*await\b",
        RegexOptions.Compiled);

    /// <summary>An <c>if (x.IsSuccess)</c> whose success arm is exclusive.</summary>
    private static readonly Regex IsSuccessGuard = new(
        @"^\s*if\s*\(\s*(?<name>[A-Za-z_]\w*)\.IsSuccess\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    ///     Rule C. An <c>ExecuteAsync</c> call whose FIRST argument is a
    ///     hard-coded empty list. Matched on the literal-and-comment-stripped
    ///     text so a PROSE mention of the shape (this file's own header, a
    ///     commented-out registration) is not a hit. The three spellings
    ///     contain no string literal, so they survive stripping intact.
    /// </summary>
    private static readonly Regex HardCodedEmptyExecuteCall = new(
        @"ExecuteAsync\s*\(\s*(?:Array\.Empty\s*<\s*string\s*>\s*\(\s*\)|System\.Array\.Empty\s*<\s*string\s*>\s*\(\s*\)|new\s+string\s*\[\s*0\s*\])",
        RegexOptions.Compiled);

    /// <summary>
    ///     Sites that match a rule and are deliberately left alone, each keyed by
    ///     (file, identifier) with the reason it is not part of this wave. The
    ///     reason is the same fact every time: the failure is either already
    ///     reported, or unreachable.
    /// </summary>
    private static readonly (string File, string Identifier, string Reason)[] KnownExemptSites =
    [
        // The palette command family (IReplCommand) has NO Result channel at
        // all: `ExecuteAsync(ReplCommandContext, CancellationToken) → Task`. A
        // `Result` read inside one of these is a local decision, not a value
        // being handed to a caller that drops it, so rule B does not describe
        // the code. The rebind is also unreachable: the agent name was just
        // read off the registered-agent list / the current session, so
        // `GetAgent` cannot fail. Rule 10 applies — a Result on a path that
        // cannot fail is a lie, and the honest form here is a plain call.
        (Path.Combine("apps", "Harbor.App.Cli", "Repl", "Commands", "AgentCommand.cs"), "agentDef",
            "IReplCommand returns Task (no Result channel); GetAgent on a name just listed as registered cannot fail"),
        (Path.Combine("apps", "Harbor.App.Cli", "Repl", "Commands", "ModelCommand.cs"), "agentDef",
            "IReplCommand returns Task (no Result channel); GetAgent on the current session's agent cannot fail"),
        // The palette session list is best-effort by design: it reads the
        // message count for every session to render one row, and reporting a
        // per-row read failure would put one error line per session into a
        // picker. The row degrades to "0 msgs" instead, which is a display
        // detail rather than a swallowed command failure.
        (Path.Combine("apps", "Harbor.App.Cli", "Repl", "Commands", "SessionsCommand.cs"), "msgs",
            "best-effort per-row message count for the palette; the read has no user-visible command surface"),
        // Boot-time theme palette load, not a command. A malformed theme.json
        // keeps the built-in palette on purpose — it must not take the boot
        // down — and at this point in the boot sequence there is no command
        // surface to report through.
        (Path.Combine("apps", "Harbor.App.Cli", "Repl", "ReplLifecycle.cs"), "initial",
            "boot-time theme load: a malformed theme falls back to the built-in palette by design, no command surface"),
    ];

    // ── Rule A: an awaited local is read ─────────────────────────────────────

    [Test]
    public async Task AwaitedResult_IsRead_BeforeItsBlockEnds()
    {
        int scanned = 0;
        var violations = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTree())
        {
            scanned++;

            foreach (Hit hit in FindUnreadAwaitedLocals(text))
            {
                if (IsExempt(relative, hit.Identifier))
                {
                    continue;
                }

                violations.Add($"{relative}:{hit.Line}: `{hit.Identifier}` is bound from an await and never read — the Result goes nowhere");
            }
        }

        await Assert.That(scanned).IsGreaterThan(50)
            .Because(
                "Non-vacuity: " + GuardedTree + " holds well over 50 source files (91 at the time of "
                + "writing). Fewer means the walk stopped matching — a renamed project, a moved marker — "
                + "and both rules below became vacuous. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(violations).IsEmpty()
            .Because(
                "A value bound from `await` and never read is a dead channel. `SlashCommandDispatcher` "
                + "assigned `var result = await reg.Execute(...)` and returned `SlashCommandOutcome.Continue`, "
                + "which has no error member — so every slash command's failure was dropped at the "
                + "dispatcher, and the handlers that returned `Result.Failure` looked correct while the "
                + "user saw nothing. Read the value (log it, or put it on the outcome) or stop returning it. "
                + "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Rule B: an `if (x.IsSuccess)` has a failure arm ─────────────────────

    [Test]
    public async Task IsSuccessGuard_HasAFailureArm()
    {
        var violations = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTree())
        {
            foreach (Hit hit in FindSuccessGuardsWithoutFailureArm(text))
            {
                if (IsExempt(relative, hit.Identifier))
                {
                    continue;
                }

                violations.Add(
                    $"{relative}:{hit.Line}: `if ({hit.Identifier}.IsSuccess)` has no failure arm — the "
                    + "failure is invisible to the user. Add an `else` / `IsFailure` branch that reports it "
                    + "(`/tree` and `/fork` in the same file are the reference shape)");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A success arm with no failure arm is the swallow. `/sessions` printed nothing at all for a "
                + "store it could not read, which reads as \"you have no sessions\" — and the `Result.Success()` "
                + "it returned afterwards hid that from every caller. Offenders:"
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Self-check 1: the scanner must not run vacuously ─────────────────────

    [Test]
    public async Task Scanner_FlagsTheDiscardedResult()
    {
        // Positive control: the exact issue-#603 shape. If the matcher ever stops
        // recognising it, both rules are decorative and read as a green light.
        const string Discarded = """
            try
            {
                _logger.LogInformation("Slash command: /{Command}", reg.Definition.Name);
                var result = await reg.Execute(ctx, args).ConfigureAwait(false);
                _logger.LogDebug("Command /{Command} completed", reg.Definition.Name);
                return SlashCommandOutcome.Continue;
            }
            """;

        // Negative control: the fixed spelling. The value is read, so rule A is
        // satisfied and rule B never applies (the guard is on IsSuccess, not on
        // IsFailure).
        const string Fixed = """
            try
            {
                var result = await reg.Execute(ctx, args).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    _logger.LogWarning("Command /{Command} failed: {Error}", reg.Definition.Name, result.Error);
                }

                return SlashCommandOutcome.Continue;
            }
            """;

        // Negative control, the case a naive scan gets wrong: the ONLY read of
        // the local lives inside an interpolation hole. Blank the string bodies
        // before counting brace depth and this identifier disappears too, so
        // the read has to be counted on the RAW text while braces are counted on
        // the stripped text.
        const string ReadInsideInterpolation = """
            int written = await WriteEventLinesAsync(client.SubscribeToEventsAsync(ct), ct);
            error.WriteLine($"stopped after {written} event(s).");
            """;

        // Positive control for rule B: the /sessions swallow.
        const string SwallowedFailure = """
            var result = await store.ListAsync().ConfigureAwait(false);
            if (result.IsSuccess)
                foreach (var s in result.Value)
                    ctx.Writer($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
            return Result.Success();
            """;

        // Negative control for rule B: the fall-through IS the failure arm.
        // ReplRunner.cs:138 is the live example — `if (consoleResult.IsSuccess)
        // return …;` then a LogWarning naming the error.
        const string FallThroughIsTheFailureArm = """
            var consoleResult = await RunCellForgeAsync(ct);
            if (consoleResult.IsSuccess)
                return consoleResult.Value;

            _logger.LogWarning("CellForge недоступен ({Reason}) — откат", consoleResult.Error);
            """;

        // Positive control for rule B: a bare `return x;` after the block is NOT
        // a failure arm. It hands the value on without looking at it, which is
        // the swallow — the shape PermissionsCommand.ClearRules and
        // ConfigCommand's `/config set` both have.
        const string BareReturnIsNotAFailureArm = """
            var saveResult = await _permissions.SaveAsync(ct).ConfigureAwait(false);
            if (saveResult.IsSuccess)
            {
                _writer("cleared");
            }

            return saveResult;
            """;

        // Positive control for rule B: an `else` at a LOWER indent belongs to an
        // enclosing `if`, not to this guard. Crediting it would silently exempt
        // every nested `if (x.IsSuccess)` in the file, which is why the
        // enclosing-block boundary is tested before the `else` is.
        const string OuterElseIsNotThisGuardsArm = """
            if (result.IsSuccess)
            {
                var agentDef = registry.GetAgent(AgentName.Create(item.Id));
                if (agentDef.IsSuccess)
                {
                    host.Agent.Initialize(host.SessionModel, agentDef.Value);
                }
            }
            else
            {
                host.Bridge.AppendSystemLine("failed");
            }
            """;

        await Assert.That(FindUnreadAwaitedLocals(Discarded).Count).IsEqualTo(1)
            .Because(
                "The positive control for rule A, verbatim from issue #603: an awaited `Result` assigned to "
                + "a local that nothing else in the file mentions. A miss means the matcher degraded and the "
                + "rule is decorative.");

        await Assert.That(FindUnreadAwaitedLocals(Fixed)).IsEmpty()
            .Because(
                "The fixed spelling must stay clean — the value is read in an `IsFailure` branch. Flagging it "
                + "would make the gate cry wolf on the very fix it asked for.");

        await Assert.That(FindUnreadAwaitedLocals(ReadInsideInterpolation)).IsEmpty()
            .Because(
                "The read lives inside `$\"…{written}…\"`. This is the assertion that proves rule A counts "
                + "identifier occurrences on the RAW text: had it counted on the literal-stripped text (the "
                + "text brace-depth needs) the hole would be blanked, the only read would vanish, and every "
                + "interpolated summary in the tree would be reported as a dead local.");

        await Assert.That(FindSuccessGuardsWithoutFailureArm(SwallowedFailure).Count).IsEqualTo(1)
            .Because(
                "The positive control for rule B, verbatim from issue #603: `/sessions` branched on success, "
                + "had no failure arm, and returned `Result.Success()` after the store had already failed.");

        await Assert.That(FindSuccessGuardsWithoutFailureArm(FallThroughIsTheFailureArm)).IsEmpty()
            .Because(
                "The code after an `if (x.IsSuccess)` block is a failure arm when it INSPECTS `x`. Treating the "
                + "fall-through as a violation would flag every backend-selection ladder in the REPL, and a "
                + "gate that fires on correct code gets switched off.");

        await Assert.That(FindSuccessGuardsWithoutFailureArm(BareReturnIsNotAFailureArm).Count).IsEqualTo(1)
            .Because(
                "A bare `return x;` is NOT a failure arm. It propagates the value without inspecting it, which "
                + "is precisely the swallow — the shape `PermissionsCommand.ClearRules` (a failed SaveAsync that "
                + "prints nothing) and `/config set` both have. If this ever starts passing, rule B has been "
                + "weakened to the point where it no longer sees the defect it was written for.");

        await Assert.That(FindSuccessGuardsWithoutFailureArm(OuterElseIsNotThisGuardsArm).Count).IsEqualTo(1)
            .Because(
                "An `else` at a lower indent belongs to an ENCLOSING if. The inner `if (agentDef.IsSuccess)` "
                + "has no failure arm of its own, and crediting the outer `else` would silently exempt every "
                + "nested `if (x.IsSuccess)` in the tree — the same reason Repl/Commands/AgentCommand.cs:60 "
                + "needs an allow-list entry rather than passing.");
    }

    // ── Rule C: a delegating registration forwards its arguments ───────────

    [Test]
    public async Task ExecuteAsyncCall_IsNotHandedAHardCodedEmptyArgumentList()
    {
        var violations = new List<string>();
        int scanned = 0;

        foreach ((string relative, string text) in ScanGuardedTree())
        {
            scanned++;

            foreach (int line in FindHardCodedEmptyExecuteCalls(text))
            {
                violations.Add(
                    $"{relative}:{line}: `ExecuteAsync` is called with a hard-coded EMPTY argument list — the "
                    + "command cannot see what the user typed. Forward the arguments the registration was "
                    + "given (the delegate's own parameter), or the argument-taking branch is unreachable.");
            }
        }

        await Assert.That(scanned).IsGreaterThan(50)
            .Because(
                "Non-vacuity, same reason as rule A: the walk must still be covering the guarded tree, or all "
                + "three rules are decorative. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(violations).IsEmpty()
            .Because(
                "Five registrations in SlashCommandDispatcher.cs bound their arguments parameter as `_` and "
                + "called `ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx))`, so `/config set model gpt-4` "
                + "took the no-args branch and printed the config dump with `Model:` still showing the OLD "
                + "value. `/auth set …`, `/model <p> <m>`, `/agent <name>` and "
                + "`/permissions <tool> <pattern> <action>` were quieter no-ops: they printed their usage and "
                + "changed nothing. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Self-check 3: rule C's matcher must not run vacuously ──────────────

    [Test]
    public async Task Scanner_FlagsTheDiscardedArguments()
    {
        // Positive control: issue #650 verbatim, two of the five registrations.
        // The argument parameter is bound as `_` in one and `args` in the other —
        // the discard happens at the call site, so the parameter's name is not
        // what the rule keys on, and neither spelling may hide it.
        const string Dropped = """
            Register(dict, "auth", (ctx, _) =>
            {
                return new AuthCommand(ctx.AuthStore, ctx.Writer)
                    .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
            });

            Register(dict, "permissions", (ctx, args) =>
            {
                return new PermissionsCommand(
                        ctx.Permissions, ctx.AgentRegistry, ctx.ConfigStore, ctx.Writer, ctx.Agent, ctx.Session)
                    .ExecuteAsync(Array.Empty<string>(), MakeCtx(ctx));
            });
            """;

        // Negative control: the fixed spelling. The same call, the arguments
        // forwarded. Rule A does not fire on it either — the lambda has no
        // awaited local — so this is a genuinely clean file under all three.
        const string Forwarded = """
            Register(dict, "config", (ctx, args) =>
            {
                return new ConfigCommand(ctx.ConfigStore, ctx.Writer)
                    .ExecuteAsync(args, MakeCtx(ctx));
            });
            """;

        // Negative control: a command that genuinely takes no arguments. The
        // `CatalogSlashCommand` DECLARATION is the live example — it has no
        // arguments to forward, and a declaration is not a call at all, so
        // flagging it would be crying wolf on correct code, which is how a gate
        // gets switched off.
        const string NoArgsOverload = """
            private sealed record CatalogSlashCommand(SlashCommandDefinition Definition) : ISlashCommand
            {
                public Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default)
                    => Task.FromResult(Result.Failure("Delegate command — use SlashCommandDispatcher to execute."));
            }
            """;

        await Assert.That(FindHardCodedEmptyExecuteCalls(Dropped).Count).IsEqualTo(2)
            .Because(
                "The positive control for rule C, verbatim from issue #650. A miss means the matcher no longer "
                + "recognises the shape and every one of the five commands can silently go back to it.");

        await Assert.That(FindHardCodedEmptyExecuteCalls(Forwarded)).IsEmpty()
            .Because(
                "The fixed spelling must stay clean — the arguments are the delegate's own parameter. Flagging "
                + "it would make the gate fire on the very fix it asked for.");

        await Assert.That(FindHardCodedEmptyExecuteCalls(NoArgsOverload)).IsEmpty()
            .Because(
                "An `ExecuteAsync` declaration is not a call, and a command with no arguments has nothing to "
                + "forward. The matcher must key on the invocation `ExecuteAsync(` immediately followed by an "
                + "empty-list first argument, not on the word.");
    }

    // ── Self-check 2: no allow-list entry may go stale ───────────────────────

    [Test]
    public async Task AllowList_EveryEntryStillMatches()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        var stale = new List<string>();

        foreach ((string relative, string identifier, string reason) in KnownExemptSites)
        {
            string full = Path.Combine(RepoPaths.RepoRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{Normalise(relative)} — exempt file no longer exists (delete the entry)");
                continue;
            }

            string text = File.ReadAllText(full);
            bool ruleA = FindUnreadAwaitedLocals(text).Any(h => h.Identifier == identifier);
            bool ruleB = FindSuccessGuardsWithoutFailureArm(text).Any(h => h.Identifier == identifier);

            if (!ruleA && !ruleB)
            {
                stale.Add(
                    $"{Normalise(relative)} — `{identifier}` no longer matches either rule, so the exemption "
                    + $"is dead ({reason})");
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "An exemption whose site has been fixed is silently dead: it would let the shape back in with "
                + "nobody watching. Delete the entry, or update its reason to describe the new shape. Stale "
                + "entries:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public async Task ScopeMarkerFiles_StillExist()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        List<string> missing =
        [
            .. KnownExemptSites
                .Select(e => e.File)
                .Concat(ScopeMarkerFiles)
                .Distinct(StringComparer.Ordinal)
                .Where(f => !File.Exists(Path.Combine(RepoPaths.RepoRoot!, f)))
        ];

        await Assert.That(missing).IsEmpty()
            .Because(
                "The guard's perimeter moved. Every file this gate was written against — scope markers and "
                + "allow-list entries alike — must still exist, or the gate is scanning a tree it no longer "
                + "describes: " + string.Join(", ", missing.Select(Normalise)));
    }

    // ── scanning ────────────────────────────────────────────────────────────

    /// <summary>A rule hit: the 1-based line and the identifier it is about.</summary>
    private readonly record struct Hit(int Line, string Identifier);

    /// <summary>Every <c>.cs</c> file under the guarded tree, minus build output.</summary>
    private static IEnumerable<(string Relative, string Text)> ScanGuardedTree()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        string dir = Path.Combine(root, GuardedTree);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file));
        }
    }

    /// <summary>Repo-relative, forward-slashed form used as the ratchet key.</summary>
    private static string Normalise(string relative) => relative.Replace('\\', '/');

    private static bool IsExempt(string relative, string identifier)
        => Array.Exists(
            KnownExemptSites,
            e => Normalise(e.File) == relative
                 && string.Equals(e.Identifier, identifier, StringComparison.Ordinal));

    /// <summary>
    ///     Rule A. A local bound from an <c>await</c> that is not read again
    ///     before the end of the block containing it. When the enclosing block
    ///     cannot be identified the scan falls back to the whole file, which
    ///     under-reports rather than over-reports — the right direction for a
    ///     ratchet.
    /// </summary>
    private static IReadOnlyList<Hit> FindUnreadAwaitedLocals(string raw)
    {
        string[] code = SplitLines(StripLiteralsAndComments(raw));
        string[] original = SplitLines(raw);
        var hits = new List<Hit>();

        for (int i = 0; i < code.Length; i++)
        {
            Match match = AwaitedLocalDeclaration.Match(code[i]);
            if (!match.Success)
            {
                continue;
            }

            string name = match.Groups["name"].Value;
            int? scopeEnd = FindEnclosingBlockEnd(code, i);

            // Counted on RAW text, never on the stripped one: an identifier read
            // only inside an interpolation hole ($"…{written}…") is a real read,
            // and the hole is exactly what StripLiteralsAndComments blanks out.
            string scope = scopeEnd is int close
                ? string.Join('\n', original[i..(close + 1)])
                : raw;

            if (CountIdentifierOccurrences(scope, name) <= 1)
            {
                hits.Add(new Hit(i + 1, name));
            }
        }

        return hits;
    }

    /// <summary>
    ///     Index of the closing brace of the block containing the declaration at
    ///     <paramref name="declIndex" />, or <c>null</c> when it cannot be found.
    ///     Walks outwards to the nearest line at a lower indent that opens a brace
    ///     it can close; a line that is itself a closing brace is the end of an
    ///     inner construct and is stepped over.
    /// </summary>
    private static int? FindEnclosingBlockEnd(string[] lines, int declIndex)
    {
        int declIndent = IndentOf(lines[declIndex]);

        for (int j = declIndex - 1; j >= 0; j--)
        {
            if (lines[j].Trim().Length == 0)
            {
                continue;
            }

            if (IndentOf(lines[j]) >= declIndent || lines[j].TrimStart().StartsWith('}'))
            {
                continue;
            }

            if (FindBlockEnd(lines, j) is int close && close > declIndex)
            {
                return close;
            }
        }

        return null;
    }

    /// <summary>
    ///     Rule B. An <c>if (x.IsSuccess)</c> whose failure arm is nowhere to be
    ///     found: no <c>else</c>, no <c>x.IsFailure</c>, no <c>!x.IsSuccess</c>,
    ///     and nothing after the block that inspects <c>x</c>.
    /// </summary>
    private static IReadOnlyList<Hit> FindSuccessGuardsWithoutFailureArm(string raw)
    {
        string[] code = SplitLines(StripLiteralsAndComments(raw));
        var hits = new List<Hit>();

        for (int i = 0; i < code.Length; i++)
        {
            Match match = IsSuccessGuard.Match(code[i]);
            if (!match.Success)
            {
                continue;
            }

            int indent = IndentOf(code[i]);
            string name = match.Groups["name"].Value;

            // A braceless body — `if (x.IsSuccess)\n    foreach (…)` — has no
            // closing brace to walk from, and starting the scan one line after
            // the guard would start it INSIDE the body, where the guard's own
            // identifier legitimately appears. Step over the whole statement.
            int? blockEnd = FindBlockEnd(code, i);
            int firstLineAfterBody = blockEnd is int close
                ? close + 1
                : FindBracelessStatementEnd(code, i) + 1;

            if (HasFailureArm(code, name, firstLineAfterBody, indent))
            {
                continue;
            }

            hits.Add(new Hit(i + 1, name));
        }

        return hits;
    }

    /// <summary>
    ///     Index of the last line of a braceless <c>if</c> body opened at
    ///     <paramref name="start" /> — the line whose statement terminator closes
    ///     it. Falls back to <paramref name="start" /> if none is found, which
    ///     makes the scan degenerate to "no failure arm".
    /// </summary>
    private static int FindBracelessStatementEnd(string[] lines, int start)
    {
        for (int i = start + 1; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.EndsWith(';') || trimmed.EndsWith('}') || trimmed.EndsWith('{'))
            {
                return i;
            }
        }

        return start;
    }

    /// <summary>
    ///     Rule C. 1-based line numbers of every <c>ExecuteAsync</c> call whose
    ///     first argument is a hard-coded empty list. Comments and string bodies
    ///     are blanked first, so this file's own header — which quotes the
    ///     defective shape at length to explain it — is not a hit. A quoted
    ///     <c>Array.Empty&lt;string&gt;()</c> inside a message string is likewise
    ///     prose, not a call.
    /// </summary>
    private static IReadOnlyList<int> FindHardCodedEmptyExecuteCalls(string raw)
    {
        string[] code = SplitLines(StripLiteralsAndComments(raw));
        var hits = new List<int>();

        for (int i = 0; i < code.Length; i++)
        {
            if (HardCodedEmptyExecuteCall.IsMatch(code[i]))
            {
                hits.Add(i + 1);
            }
        }

        return hits;
    }

    /// <summary>
    ///     Walks forward from the end of an <c>if</c> block, to the end of its
    ///     enclosing block, looking for anything that treats the guard's failure
    ///     as a case to be handled.
    /// </summary>
    private static bool HasFailureArm(string[] code, string name, int from, int indent)
    {
        var identifier = new Regex($@"\b{Regex.Escape(name)}\b");
        var negative = new Regex($@"!\s*{Regex.Escape(name)}\.IsSuccess\b");
        var failureArm = new Regex($@"\b{Regex.Escape(name)}\.IsFailure\b");
        var passThrough = new Regex($@"return\s+{Regex.Escape(name)}\s*;");

        for (int j = from; j < code.Length; j++)
        {
            string line = code[j];
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // The enclosing block ended. This is checked BEFORE the `else`
            // test on purpose: an `else` at a lower indent belongs to an
            // enclosing `if`, not to this guard, and crediting it here would
            // silently exempt every nested `if (x.IsSuccess)` in the file.
            // Likewise the `}` that closes the enclosing block.
            if (IndentOf(line) < indent || trimmed.StartsWith('}'))
            {
                if (IndentOf(line) < indent || trimmed == "}")
                {
                    return false;
                }

                continue;
            }

            if (trimmed.StartsWith("else", StringComparison.Ordinal)
                || negative.IsMatch(line)
                || failureArm.IsMatch(line))
            {
                return true;
            }

            // The fall-through is the failure arm when the code after the block
            // INSPECTS the value. A bare `return x;` does not — it hands the
            // value on without looking at it, which is the swallow.
            if (identifier.IsMatch(passThrough.Replace(line, string.Empty)))
            {
                return true;
            }

            // Control left the block with the value unexamined.
            if (trimmed.StartsWith("return", StringComparison.Ordinal)
                || trimmed.StartsWith("throw", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return false;
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

            // A `;` before any `{` means the statement has no block — a
            // braceless body such as `if (x.IsSuccess)\n    _writer(…);`.
            if (lines[i].Contains(';'))
            {
                return null;
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

    /// <summary>
    ///     Blanks the CONTENTS of string and char literals, verbatim/raw-string
    ///     bodies and comments, preserving line structure and the quote
    ///     characters. Brace-depth counting must never see an interpolation hole:
    ///     <c>$"…{x}…"</c> has balanced braces on one line, so an unstripped
    ///     count closes the block immediately and the scan walks off the end of
    ///     the <c>if</c> it was measuring. That is not hypothetical — it is what
    ///     made this scanner mis-read <c>AuthCommand</c>'s <c>if/else</c> pair as
    ///     having no <c>else</c> on the first run.
    /// </summary>
    private static string StripLiteralsAndComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    builder.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
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

            if (text.AsSpan(i).StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                builder.Append("\"\"\"");
                i += 3;
                while (i < text.Length && !text.AsSpan(i).StartsWith("\"\"\"", StringComparison.Ordinal))
                {
                    builder.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                builder.Append("\"\"\"");
                i = Math.Min(i + 3, text.Length);
                continue;
            }

            if (c is '"' or '\'')
            {
                char quote = c;
                builder.Append(quote);
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        builder.Append("  ");
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        builder.Append(quote);
                        i++;
                        break;
                    }

                    builder.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static int CountIdentifierOccurrences(string text, string identifier)
        => Regex.Matches(text, $@"\b{Regex.Escape(identifier)}\b", RegexOptions.Compiled).Count;
}
