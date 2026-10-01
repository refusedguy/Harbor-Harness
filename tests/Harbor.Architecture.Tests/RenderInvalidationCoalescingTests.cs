// RenderInvalidationCoalescingTests.cs — the contract for RENDER invalidation
// coalescing on the CellForge REPL frame loop (issue #396, epic #47/S5).
//
// THE QUESTION #396 ASKS
// ---------------------
// "Coalesce RENDER invalidations, not domain events — priority lanes for
//  cancel/approval." The issue's premise is that "rendering is modelled as an
//  ordinary domain-event consumer, so a streaming burst of N deltas produces N
//  invalidations competing with control-plane messages in the same queue", and it
//  asks for a SECOND class of message plus a priority lane.
//
// THE MEASURED ANSWER: THE COALESCING ALREADY EXISTS, AND THERE IS NO QUEUE TO
// LANE.
// -----------------------------------------------------------------------
// The frame loop (`apps/Harbor.App.Cli/Repl/ReplLifecycle.cs` `LoopAsync`) has
// TWO channels and they are deliberately different kinds of thing:
//
//   `_events`  Channel<AgentEvent>   LOSSLESS. Every bus event, in arrival
//                                  order, drained by `while (TryRead)` BEFORE
//                                  the frame renders. Never coalesced, never
//                                  reordered, never dropped (unbounded, and
//                                  `TryWrite`'s bool is not the mechanism —
//                                  there is no bounded drop mode to hit).
//   `_wake`    Channel<object?>      LEVEL-TRIGGERED. Every write is the
//                                  literal `null`; `DrainWake()` discards the
//                                  tokens' CONTENTS and keeps only "something
//                                  changed". N writes collapse to one repaint.
//
// So an approval/cancel/permission invalidation is never "behind" a burst of
// visual invalidations: there is no visual queue for it to be behind. It is in
// `_events`, and `_events` is drained to empty in the same loop iteration that
// renders the burst — one `while` loop, one frame, arrival order preserved. The
// control plane is not a lane; it is the lossless half of a two-channel split
// that already exists.
//
// WHY A SOURCE SCAN
// -----------------
// The two properties above are not expressible in the compiled type graph: both
// channels are `Channel<T>` with a `TryWrite`, so `Channel<AgentEvent>` and
// `Channel<RenderInvalidation>` would be indistinguishable to a reference-based
// rule. What distinguishes them is what the WRITE CARRIES — and that is textual.
// This follows the mechanism `SlashResultChannelTests` and
// `ResultTryAdoptionTests` already established in this project.
//
// THE FOUR RULES
// --------------
//
//   A. THE WAKE CHANNEL CARRIES NO PAYLOAD. Every `_wake.Writer.TryWrite(x)` must
//      pass `null`. This is the rule that encodes the ISSUE'S OWN MAIN RISK, and
//      it is mechanical rather than advisory: `DrainWake` reads tokens and throws
//      their contents away (`while (TryRead(out _))`). A payload written into
//      `_wake` is not queued for later — it is DISCARDED BY THE COALESCING, and
//      the user sees a stale frame with no error anywhere. Today all 36 writes
//      pass `null`; the rule makes that a property of the tree rather than a
//      coincidence nobody is watching.
//
//   B1. THE EVENT CHANNEL IS UNBOUNDED. A bounded `_events` turns the ignored
//       `TryWrite` bool into a silent domain-event drop under back-pressure —
//       exactly "domain events are never dropped", the first line of the issue's
//       acceptance criteria, arriving through the one door a source scan can
//       watch.
//
//   B2. THE EVENT CHANNEL CARRIES NO SENTINEL. An event-channel write must be
//       handed the bus payload. A `null` / `default` / `new …` there is a render
//       invalidation smuggled into the lossless channel: the single queue the
//       issue says must not exist, and one that would additionally be read as an
//       `AgentEvent`.
//
//   C. PUBLISH STATE BEFORE WAKE. In any brace block that writes BOTH channels,
//       the first `_events` write must precede the first `_wake` write — the
//       local-queue rule from the #47 comment. Reversed, a renderer can drain the
//       wake, read an `_events` that does not hold the payload yet, paint a frame
//       that misses it, and then go idle: the event is stranded until the next
//       unrelated heartbeat, i.e. a stale screen.
//
// NON-VACUITY — THE #591 LESSON
// -----------------------------
// #591 is the night this project lost a night to: an instrument that HALVED a
// rule twice and returned plausible zeros, so a real defect read as a pass. Four
// things here are built to make that impossible to repeat:
//
//   1. Every rule has a POSITIVE control (a `const string` snippet in the exact
//      violating shape) asserted to be FLAGGED, next to a negative control
//      asserted to be CLEAN. A matcher that degraded would fail loudly instead of
//      going quiet.
//   2. The main test asserts the scan FOUND the sites — ≥ 20 wake writes and ≥ 1
//      event write and ≥ 1 block that writes both. "No violations" is only
//      meaningful next to "the thing being counted was counted"; a rule that
//      matches nothing returns an empty violation list forever.
//   3. Comments and string/char/verbatim-string bodies are blanked before any
//      match. This file's own header quotes the violating shapes at length, and
//      the merged #970 recorded the other failure direction — a naive scan that
//      MATCHED fixture strings, 245 findings collapsing to 87 real ones. A rule
//      that flags this file's prose would be a gate nobody keeps.
//   4. The guarded files are named as scope markers and asserted to still exist,
//      so a moved file cannot turn the walk into an empty one.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Source-level guard for the CellForge REPL's render-invalidation contract:
///     <c>_events</c> is a lossless domain-event channel, <c>_wake</c> is a
///     payload-free level-triggered invalidation signal, and state is published
///     before the wake. Issue #396.
/// </summary>
public sealed class RenderInvalidationCoalescingTests
{
    /// <summary>
    ///     The tree this gate governs: the composition root that owns the frame
    ///     loop and both of its channels.
    /// </summary>
    private const string GuardedTree = "apps/Harbor.App.Cli";

    /// <summary>
    ///     Files whose existence proves the scan still covers the perimeter it
    ///     was written for. <c>SetupChecklistController</c> is here because it is
    ///     the ONLY off-thread invalidation producer in the tree — the one site
    ///     that must pair the dirty-sequence bump with the wake (it does:
    ///     <c>Interlocked.Increment(ref _host._frameDirtySeq)</c> immediately
    ///     before <c>_wake.Writer.TryWrite(null)</c>), so if it moves, the note
    ///     in <see cref="WakeChannel_CarriesNoPayload" /> is stale.
    /// </summary>
    private static readonly string[] ScopeMarkerFiles =
    [
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "CellForgeReplRunner.cs"),
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "ReplLifecycle.cs"),
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "SetupChecklistController.cs"),
        Path.Combine("apps", "Harbor.App.Cli", "Repl", "ReplInputLoop.cs"),
    ];

    /// <summary>The two channel fields. Symbols, not line numbers: a rename
    /// keeps the guard attached (the merged #970 measured that naming a SYMBOL
    /// survives a rename where naming a line does not).</summary>
    private const string WakeChannel = "_wake";

    private const string EventChannel = "_events";

    /// <summary>Number of wake writes the tree holds today (36). A floor, not
    /// an exact count: the point is that the scan SAW them.</summary>
    private const int WakeWriteFloor = 20;

    /// <summary>
    ///     <c>&lt;channel&gt;.Writer.TryWrite(&lt;arg&gt;)</c> on a single
    ///     line, argument captured. Every write in the tree is on one line; a
    ///     multi-line <c>TryWrite(\n    evt)</c> is under-reported, which is the
    ///     right direction for a ratchet (it cannot invent a violation).
    /// </summary>
    private static readonly Regex ChannelWrite = new(
        @"(?<channel>[A-Za-z_]\w*)\.Writer\.TryWrite\s*\(\s*(?<arg>[^()]*?)\s*\)",
        RegexOptions.Compiled);

    /// <summary>The declaration of the lossless channel.</summary>
    private static readonly Regex EventChannelDeclaration = new(
        $@"Channel<\s*\w+\s*>\s*{Regex.Escape(EventChannel)}\s*=\s*Channel\.(?<factory>Create\w+)",
        RegexOptions.Compiled);

    // ── Rule A: the wake channel carries no payload ────────────────────────

    /// <summary>
    ///     Rule A. <c>DrainWake</c> reads every pending wake token and discards
    ///     its contents — that is what makes the signal level-triggered and what
    ///     makes N writes cost one repaint. The cost of the same design is that a
    ///     payload put into <c>_wake</c> is not deferred work; it is thrown away.
    /// </summary>
    [Test]
    public async Task WakeChannel_CarriesNoPayload()
    {
        int scanned = 0;
        int writes = 0;
        var violations = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTree())
        {
            scanned++;

            foreach (WriteSite hit in FindWrites(text, WakeChannel))
            {
                writes++;
                if (!IsNullLiteral(hit.Argument))
                {
                    violations.Add(
                        $"{relative}:{hit.Line}: `_wake.Writer.TryWrite({hit.Argument})` puts a payload into the "
                        + "level-triggered wake channel. `DrainWake` reads every pending token and DISCARDS its "
                        + "contents, so this invalidation is not queued for a later frame — it is dropped by the "
                        + "coalescing and the user keeps the stale frame. Wake with `null`; if the thing that "
                        + "changed must survive the frame, it belongs in the lossless `_events` channel.");
                }
            }
        }

        await Assert.That(scanned).IsGreaterThan(50)
            .Because(
                "Non-vacuity: " + GuardedTree + " holds well over 50 source files. Fewer means the walk stopped "
                + "matching — a renamed project, a moved marker — and every rule here became vacuous. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(writes).IsGreaterThanOrEqualTo(WakeWriteFloor)
            .Because(
                "Non-vacuity, and the part #591 is about: an EMPTY violation list is only evidence when the scan "
                + "actually counted something. The tree holds " + WakeWriteFloor + "+ writes to the wake channel "
                + "(36 at the time of writing). If this finds fewer, the matcher stopped recognising them and rule A "
                + "is reporting a clean tree it never looked at.");

        await Assert.That(violations).IsEmpty()
            .Because(
                "A render invalidation written into the coalescing wake channel is silently discarded — the user "
                + "sees the previous frame and nothing reports it. Offenders:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    // ── Rule B: the event channel is lossless ─────────────────────────────

    [Test]
    public async Task EventChannel_IsUnboundedAndCarriesNoSentinel()
    {
        int scanned = 0;
        int eventWrites = 0;
        var violations = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTree())
        {
            scanned++;

            // B2 — no sentinel on the lossless channel.
            foreach (WriteSite hit in FindWrites(text, EventChannel))
            {
                eventWrites++;
                if (IsSentinel(hit.Argument))
                {
                    violations.Add(
                        $"{relative}:{hit.Line}: `_events.Writer.TryWrite({hit.Argument})` puts a constant into the "
                        + "DOMAIN-EVENT channel. Render invalidations must not travel here: this channel is drained "
                        + "to empty every frame and read as an `AgentEvent`, so a sentinel is a repaint request that "
                        + "also corrupts the arrival-order stream the control plane depends on. Wake with `null` on "
                        + "`_wake` instead.");
                }
            }

            // B1 — unbounded, or the ignored TryWrite bool becomes a drop.
            foreach (Match match in EventChannelDeclaration.Matches(StripLiteralsAndComments(text)))
            {
                string factory = match.Groups["factory"].Value;
                if (!string.Equals(factory, "CreateUnbounded", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{relative}: `_events` is created with `Channel.{factory}` — a BOUNDED domain-event "
                        + "channel. Under back-pressure `TryWrite` returns false, the return value is not checked, "
                        + "and domain events are dropped with no signal anywhere. The first acceptance line of "
                        + "#396 is that they never are. Use `Channel.CreateUnbounded`.");
                }
            }
        }

        await Assert.That(scanned).IsGreaterThan(50)
            .Because(
                "Same non-vacuity floor as rule A: the walk must still be covering the guarded tree. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(eventWrites).IsGreaterThanOrEqualTo(1)
            .Because(
                "Non-vacuity: the rule is about writes to `_events`, and the scan found none. The frame loop's bus "
                + "pump is the single producer (ReplLifecycle.cs). If that moved, this rule is watching an empty "
                + "set and its clean result says nothing.");

        await Assert.That(violations).IsEmpty()
            .Because(
                "Both halves of \"domain events are never dropped\" pass through these two doors: a bounded channel "
                + "drops silently on back-pressure, and a sentinel turns a repaint request into a fake AgentEvent. "
                + "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ── Rule C: publish state before wake ──────────────────────────────────

    [Test]
    public async Task StateIsPublishedBeforeTheWake_InEveryBlockThatWritesBoth()
    {
        int scanned = 0;
        int comparedBlocks = 0;
        var violations = new List<string>();

        foreach ((string relative, string text) in ScanGuardedTree())
        {
            scanned++;

            foreach (BlockOrder order in FindBlocksWritingBothChannels(text))
            {
                comparedBlocks++;
                if (order.FirstEventLine < order.FirstWakeLine)
                {
                    continue;
                }

                violations.Add(
                    $"{relative}:{order.FirstWakeLine}: `_wake.Writer` is written BEFORE `_events.Writer` inside the "
                    + "same block. The renderer drains wakes BEFORE reading the event queue, so waking first lets "
                    + "it paint a frame out of a queue that does not hold the payload yet — and then go idle with "
                    + "the event stranded. Publish the state, then wake.");
            }
        }

        await Assert.That(scanned).IsGreaterThan(50)
            .Because(
                "Same non-vacuity floor as the other rules. RepoPaths.RepoRoot was "
                + (RepoPaths.RepoRoot is null ? "null" : "found") + ".");

        await Assert.That(comparedBlocks).IsGreaterThanOrEqualTo(1)
            .Because(
                "Non-vacuity: no block writes BOTH channels, so rule C compared nothing. The frame loop's bus pump "
                + "(ReplLifecycle.cs) is the block that publishes an event and then wakes; if it is refactored into "
                + "two blocks or one call, this assertion is the only thing that notices the rule stopped applying.");

        await Assert.That(violations).IsEmpty()
            .Because(
                "The #47 local-queue rule is publish-state-BEFORE-wake, and the render loop is where a violation "
                + "becomes a stale screen rather than a lost log line. Offenders:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    // ── Self-checks: every matcher must fire on the shape it exists for ───

    [Test]
    public async Task Scanners_FlagTheShapesTheyExistFor()
    {
        // Rule A positive control. Both spellings are real ways to reintroduce
        // the bug the issue is about: a render invalidation queued behind the
        // coalescing, and a revision number that the drain throws away.
        const string PayloadInWake = """
            host._wake.Writer.TryWrite(new RenderInvalidation(rect, revision));
            """;

        const string RevisionInWake = """
            _wake.Writer.TryWrite(rev);
            """;

        // Rule A negative control: the live spelling. Thirty-six of them.
        const string NullWake = """
            host._wake.Writer.TryWrite(null);
            """;

        // Rule A negative control, and the #970 lesson made concrete: the shape
        // quoted inside prose is prose, not code. This file's own header, and
        // any commit message or comment in the tree that explains the rule,
        // must not be reported.
        const string ShapeInsideAComment = """
            // Never write a revision here: _wake.Writer.TryWrite(rev) is dropped by DrainWake.
            Log.Warn($"_wake.Writer.TryWrite(rev) must never appear in the tree");
            """;

        // Rule B2 positive control: a repaint request smuggled into the lossless
        // channel. `default!` is the compilable spelling — `null` would not even
        // bind to `Channel<AgentEvent>.TryWrite`, which is why a rule that only
        // banned `null` would have been decorative.
        const string SentinelInEvents = """
            host._events.Writer.TryWrite(default!);
            """;

        // Rule B1 positive control: the bounded-channel drop.
        const string BoundedEventChannel = """
            internal readonly Channel<AgentEvent> _events = Channel.CreateBounded<AgentEvent>(
                new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropWrite });
            """;

        const string UnboundedEventChannel = """
            internal readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            """;

        // Rule C positive control: the swap, verbatim in shape.
        const string WakeBeforeEvent = """
            host.EventBus.Subscribe((evt, _) =>
            {
                host._wake.Writer.TryWrite(null);
                host._events.Writer.TryWrite(evt);
                return ValueTask.CompletedTask;
            });
            """;

        // Rule C negative control: the live spelling (state, then wake).
        const string EventBeforeWake = """
            host.EventBus.Subscribe((evt, _) =>
            {
                host._events.Writer.TryWrite(evt);
                host._wake.Writer.TryWrite(null);
                return ValueTask.CompletedTask;
            });
            """;

        // Rule C negative control: two writes in DIFFERENT blocks. Nothing here
        // orders them against each other and nothing should try — a wake on the
        // frame thread and an event write inside the bus pump are unrelated, and
        // flagging them would be crying wolf on correct code.
        const string SeparateBlocks = """
            private void Pump() => host._events.Writer.TryWrite(evt);

            private void RequestFrame()
            {
                Interlocked.Increment(ref host._frameDirtySeq);
                host._wake.Writer.TryWrite(null);
            }
            """;

        await Assert.That(FindWrites(PayloadInWake, WakeChannel).Count).IsEqualTo(1)
            .Because(
                "Positive control for rule A. A `RenderInvalidation` record handed to the level-triggered channel is "
                + "the exact shape #396 warns about; if this ever passes, rule A cannot see a queued invalidation and "
                + "the whole guard is decorative.");

        await Assert.That(FindWrites(RevisionInWake, WakeChannel).Count).IsEqualTo(1)
            .Because(
                "Positive control for rule A, second spelling. A revision number is the most tempting payload — it "
                + "looks like the \"repaint the current revision\" the issue asks for — and `DrainWake` discards it, "
                + "so the renderer would paint whatever state happens to be current instead of the revision the "
                + "producer intended.");

        await Assert.That(FindWrites(NullWake, WakeChannel).Count).IsEqualTo(1)
            .Because(
                "The live spelling must be COUNTED (it is a write) and not FLAGGED. `TryWrite(null)` is the only "
                + "argument rule A accepts, and it is what all 36 sites in the tree use.");

        await Assert.That(FindFlaggedWrites(PayloadInWake, WakeChannel)).IsEmpty()
            .Because(
                "Rule A's own matcher on its own positive control: the write is counted AND reported. Counting and "
                + "reporting are separate statements and a change to the counting regex must not silently disarm the "
                + "reporting one.");

        await Assert.That(FlaggedWakes(ShapeInsideAComment)).IsEmpty()
            .Because(
                "A quoted shape inside a comment or a string is prose. This is the #970 lesson pointed the other way: "
                + "that audit's scan MATCHED fixture strings and reported 245 findings against 87 real ones. This "
                + "file quotes the violating shape eight times in its own header; if the scan is not stripped, the "
                + "gate fires on its own documentation and gets switched off.");

        await Assert.That(FindWrites(SentinelInEvents, EventChannel).Count(s => IsSentinel(s.Argument))).IsEqualTo(1)
            .Because(
                "Positive control for rule B2. A render invalidation smuggled into the domain-event channel would be "
                + "drained every frame AND read as an AgentEvent — the one queue the issue says must not exist. If "
                + "this stops matching, rule B2 has no teeth.");

        await Assert.That(BoundedFactories(BoundedEventChannel)).IsEqualTo(1)
            .Because(
                "Positive control for rule B1. A bounded channel with DropWrite plus an unchecked `TryWrite` is a "
                + "silent domain-event drop — the failure #396's first acceptance line is written to prevent.");

        await Assert.That(BoundedFactories(UnboundedEventChannel)).IsEmpty()
            .Because(
                "The live spelling must stay clean. Flagging it would make rule B1 fire on the very declaration the "
                + "issue's acceptance criteria require.");

        await Assert.That(FindBlocksWritingBothChannels(WakeBeforeEvent).Count).IsEqualTo(1)
            .Because(
                "Positive control for rule C, the swap. Publishing the wake before the state is what strands the "
                + "event: the renderer drains wakes, reads a queue the producer has not written yet, paints, and "
                + "goes idle.");

        await Assert.That(FindBlocksWritingBothChannels(WakeBeforeEvent)[0].FirstEventLine
                > FindBlocksWritingBothChannels(WakeBeforeEvent)[0].FirstWakeLine)
            .IsTrue()
            .Because(
                "The control must actually be reported as mis-ordered, not merely found. A matcher that returned the "
                + "block without an ordering verdict would pass the count assertion above and catch nothing.");

        await Assert.That(FindBlocksWritingBothChannels(EventBeforeWake).Count).IsEqualTo(1)
            .Because(
                "The live spelling is still ONE block writing both channels — it just orders them correctly. If this "
                + "returned zero, rule C would have gone vacuous rather than green.");

        await Assert.That(FindBlocksWritingBothChannels(SeparateBlocks).Count).IsEqualTo(0)
            .Because(
                "Writes in different blocks are not comparable, and rule C must not pretend they are. The frame "
                + "thread's own wake and the bus pump's event write are unrelated, and a rule that paired them "
                + "would fire on correct code.");

        await Assert.That(FindBlocksWritingBothChannels(EventBeforeWake)[0].FirstEventLine
                < FindBlocksWritingBothChannels(EventBeforeWake)[0].FirstWakeLine)
            .IsTrue()
            .Because(
                "The negative control for rule C's verdict: state first, wake second. This is the order the frame "
                + "loop depends on and the reason the rule exists.");
    }

    [Test]
    public async Task ScopeMarkerFiles_StillExist()
    {
        if (RepoPaths.RepoRoot is null)
        {
            return;
        }

        List<string> missing =
            [.. ScopeMarkerFiles.Where(f => !File.Exists(Path.Combine(RepoPaths.RepoRoot!, f)))];

        await Assert.That(missing).IsEmpty()
            .Because(
                "The guard's perimeter moved. Every file this gate was written against must still exist, or the scan "
                + "is walking a tree it no longer describes: " + string.Join(", ", missing.Select(m => m.Replace('\\', '/'))));
    }

    // ── scanning ────────────────────────────────────────────────────────────

    private readonly record struct WriteSite(int Line, string Argument);

    private readonly record struct BlockOrder(int FirstEventLine, int FirstWakeLine);

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

    /// <summary>
    ///     Every <c>&lt;name&gt;.Writer.TryWrite(…)</c> on the named channel,
    ///     counted on literal- and comment-stripped text.
    /// </summary>
    private static IReadOnlyList<WriteSite> FindWrites(string raw, string channel)
    {
        string[] code = SplitLines(StripLiteralsAndComments(raw));
        var hits = new List<WriteSite>();

        for (int i = 0; i < code.Length; i++)
        {
            foreach (Match match in ChannelWrite.Matches(code[i]))
            {
                if (!string.Equals(match.Groups["channel"].Value, channel, StringComparison.Ordinal))
                {
                    continue;
                }

                hits.Add(new WriteSite(i + 1, match.Groups["arg"].Value.Trim()));
            }
        }

        return hits;
    }

    /// <summary>Rule A's verdict: the wake writes whose argument is not <c>null</c>.</summary>
    private static IReadOnlyList<WriteSite> FlaggedWakes(string raw) =>
        [.. FindWrites(raw, WakeChannel).Where(w => !IsNullLiteral(w.Argument))];

    /// <summary>Channel factories that are NOT <c>CreateUnbounded</c> — rule B1's verdict.</summary>
    private static IReadOnlyList<string> BoundedFactories(string raw) =>
    [
        .. EventChannelDeclaration
            .Matches(StripLiteralsAndComments(raw))
            .Select(m => m.Groups["factory"].Value)
            .Where(f => !string.Equals(f, "CreateUnbounded", StringComparison.Ordinal))
    ];

    /// <summary>
    ///     The innermost brace block around every line that writes either
    ///     channel, reduced to the blocks that write BOTH. Block identity is the
    ///     line that OPENED it, which is stable regardless of how the body is
    ///     formatted.
    /// </summary>
    private static IReadOnlyList<BlockOrder> FindBlocksWritingBothChannels(string raw)
    {
        string[] code = SplitLines(StripLiteralsAndComments(raw));
        int[] depthBefore = BuildDepthBefore(code);
        var eventByBlock = new Dictionary<int, int>();
        var wakeByBlock = new Dictionary<int, int>();

        for (int i = 0; i < code.Length; i++)
        {
            foreach (Match match in ChannelWrite.Matches(code[i]))
            {
                int? open = EnclosingOpenLine(code, depthBefore, i);
                if (open is not { } openLine)
                {
                    continue;
                }

                bool isWake = string.Equals(match.Groups["channel"].Value, WakeChannel, StringComparison.Ordinal);
                Dictionary<int, int> target = isWake ? wakeByBlock : eventByBlock;
                if (!target.TryGetValue(openLine, out int line))
                {
                    target[openLine] = i + 1;
                }
            }
        }

        var both = new List<BlockOrder>();
        foreach ((int openLine, int eventLine) in eventByBlock)
        {
            if (wakeByBlock.TryGetValue(openLine, out int wakeLine))
            {
                both.Add(new BlockOrder(eventLine, wakeLine));
            }
        }

        both.Sort(static (a, b) => a.FirstEventLine.CompareTo(b.FirstEventLine));
        return both;
    }

    /// <summary>Brace depth in force immediately before each line.</summary>
    private static int[] BuildDepthBefore(string[] code)
    {
        var depth = new int[code.Length];
        int running = 0;

        for (int i = 0; i < code.Length; i++)
        {
            depth[i] = running;
            running += BraceDelta(code[i]);
        }

        return depth;
    }

    /// <summary>
    ///     Line that opened the innermost brace block containing line
    ///     <paramref name="index" />, or <c>null</c> at file scope (a top-level
    ///     statement has no enclosing block and therefore no ordering partner).
    /// </summary>
    private static int? EnclosingOpenLine(string[] code, int[] depthBefore, int index)
    {
        int here = depthBefore[index];
        if (here <= 0)
        {
            return null;
        }

        for (int i = index - 1; i >= 0; i--)
        {
            if (depthBefore[i] < here && code[i].Contains('{'))
            {
                return i;
            }
        }

        return null;
    }

    private static int BraceDelta(string line) =>
        line.Count(ch => ch == '{') - line.Count(ch => ch == '}');

    private static bool IsNullLiteral(string argument) =>
        string.Equals(argument, "null", StringComparison.Ordinal);

    /// <summary>
    ///     Rule B2's verdict: a constant handed to the domain-event channel.
    ///     <c>null</c> and <c>default</c> are the two that bind to
    ///     <c>Channel&lt;AgentEvent&gt;</c>; <c>new …</c> is a constructed fake
    ///     event, which is the same mistake wearing a different hat.
    /// </summary>
    private static bool IsSentinel(string argument)
    {
        if (argument.Length == 0)
        {
            return false;
        }

        string trimmed = argument.TrimEnd('!', '?').Trim();
        return string.Equals(trimmed, "null", StringComparison.Ordinal)
               || string.Equals(trimmed, "default", StringComparison.Ordinal)
               || trimmed.StartsWith("new ", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Blanks the CONTENTS of string and char literals, verbatim/raw-string
    ///     bodies and comments, preserving line structure and the quote
    ///     characters — the same routine <c>SlashResultChannelTests</c> uses, for
    ///     the same reason.
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
}