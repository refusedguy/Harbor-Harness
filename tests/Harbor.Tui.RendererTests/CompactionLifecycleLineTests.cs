namespace Harbor.Tui.RendererTests;

using Harbor.Abstractions.Events;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.NickConsoleEx;
using Harbor.Tui.Notifications;
using Harbor.Tui.RendererTests.Support;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Issue #840 — the <b>line</b> half of the compaction lifecycle, in the
///     three renderers that own their own compaction output.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a different guard from #839's, on purpose.</b> #773/#839
///         guarded the status <b>cell</b>: <c>ChatAppReducer.Reduce</c> and
///         <c>StatusBarViewModel.UpdateFromEventAsync</c> read a <b>status
///         string</b> off a projection. Both of those arms landed in #839, and
///         this file must not restate them — a reducer or view-model assertion
///         would be green on today's tree and would prove nothing here.
///     </para>
///     <para>
///         The gap in #840 is one shape up: <c>CompactionStartedEvent</c> and
///         <c>CompactionCompletedEvent</c> each reach a handler that WRITES
///         something, and <c>CompactionFailedEvent</c> reaches none. So every
///         assertion below reads a renderer's <b>output</b> — the bytes on the
///         plain writer, the composed cells of the headless driver, the argv
///         the notification runner recorded. Nothing here reads a projection.
///     </para>
///     <para>
///         <b>The wording is the contract.</b> <c>compaction failed: … —
///         continuing on truncated history</c> is #839's
///         <c>ChatAppReducer.OnCompactionFailed</c> string, asserted here as a
///         literal rather than read back from whatever formatter produces it: a
///         guard that compared the renderer to the formatter would pass on any
///         wording at all, which is exactly the thing that has to be pinned.
///     </para>
///     <para>
///         <b>Why the third member has to be told.</b>
///         <c>CompactionBehavior.PublishFailureAsync</c> publishes the failure
///         and returns <c>TruncationFallback: true</c> — the run CONTINUES on a
///         shortened history, and its own comment calls the fallback
///         "irreversible". Nothing about the run stopping tells the user their
///         context was cut, so if the renderers say nothing the degradation is
///         invisible in every surface at once.
///     </para>
/// </remarks>
public class CompactionLifecycleLineTests
{
    /// <summary>The single user-visible sentence a compaction failure owes the user.</summary>
    private const string FailureLine =
        "compaction failed: summarizer timed out — continuing on truncated history";

    private static CompactionFailedEvent Failure() => new("s1", "summarizer timed out");

    // ── AnsiPlain — the renderer AGENTS.md documents for pipes and CI ──────

    [Test]
    public async Task AnsiPlain_CompactionFailed_WritesTheTruncationWarning()
    {
        using var writer = new StringWriter();
        using var renderer = new PlainTuiRenderer(writer, new UiStore());
        await renderer.InitializeAsync();

        await renderer.RenderAsync(Failure());

        string output = writer.ToString();
        await Assert.That(output).Contains(FailureLine)
            .Because("HARBOR_TUI=plain is the renderer a CI transcript diff compares "
                   + "against, so a compaction that silently truncated the history "
                   + "produced byte-identical output to a healthy run — the one "
                   + "surface where the loss is provable after the fact");
    }

    [Test]
    public async Task AnsiPlain_CompactionFailed_KeepsTheFailureOffTheSuccessLine()
    {
        using var writer = new StringWriter();
        using var renderer = new PlainTuiRenderer(writer, new UiStore());
        await renderer.InitializeAsync();

        await renderer.RenderAsync(new CompactionStartedEvent("s1"));
        await renderer.RenderAsync(Failure());

        string output = writer.ToString();
        await Assert.That(output).Contains("[compacting context...]")
            .Because("the started sibling must still paint, or this fix replaced the "
                   + "lifecycle instead of completing it");
        await Assert.That(output).Contains(FailureLine)
            .Because("started-then-failed is the sequence a user actually hits, and it is "
                   + "the one that has to read as a story: the context was being "
                   + "compacted, that did not work, here is what happened to it");
        await Assert.That(output).DoesNotContain("[compacted:")
            .Because("a truncation is not a compaction: reusing the success marker would "
                   + "tell the reader pruned N messages when nothing was pruned");
    }

    /// <summary>
    ///     The form #839's guard cannot take: a <b>census</b>, not a status
    ///     string. Every <c>Compaction*</c> member of the <c>AgentEvent</c> union
    ///     is named here, and this test fails if the union grows a fourth one
    ///     that this renderer never narrates. #840 is not "the failure arm is
    ///     missing" — it is "the lifecycle has three members and one renderer
    ///     knows two", which is a fact about the union that no per-event
    ///     assertion keeps true once somebody adds the next member.
    /// </summary>
    [Test]
    public async Task AnsiPlain_EveryCompactionMember_NarratesItself()
    {
        var members = new Dictionary<string, Func<AgentEvent>>(StringComparer.Ordinal)
        {
            ["CompactionStartedEvent"] = () => new CompactionStartedEvent("s1"),
            ["CompactionCompletedEvent"] = () =>
                new CompactionCompletedEvent("s1", "summary", 3, 100, TimeSpan.FromSeconds(1)),
            ["CompactionFailedEvent"] = () => Failure(),
        };

        string census = string.Join(
            ",",
            typeof(AgentEvent).Assembly
                .GetTypes()
                .Where(t => typeof(AgentEvent).IsAssignableFrom(t)
                            && t.Name.StartsWith("Compaction", StringComparison.Ordinal))
                .Select(t => t.Name)
                .Order(StringComparer.Ordinal));

        await Assert.That(census)
            .IsEqualTo(string.Join(",", members.Keys.Order(StringComparer.Ordinal)))
            .Because("this is the half of the gap no per-event assertion holds: a new "
                   + "Compaction* member of the AgentEvent union arriving with no arm in "
                   + "these renderers. Name it above and say who narrates it — left out, "
                   + "it reads as 'nobody says anything', which is this bug");

        foreach ((string name, Func<AgentEvent> make) in
                 members.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            using var writer = new StringWriter();
            using var renderer = new PlainTuiRenderer(writer, new UiStore());
            await renderer.InitializeAsync();

            await renderer.RenderAsync(make());

            await Assert.That(writer.ToString()).IsNotEmpty()
                .Because($"{name} reaches a handler in this renderer only if that handler "
                       + "writes something; an empty writer IS the shape #840 is about, and "
                       + "no assertion reading a status string can see it");
        }
    }

    // ── NickConsoleEx ─────────────────────────────────────────────────────

    [Test]
    public async Task NickConsoleEx_CompactionFailed_WritesTheTruncationWarning()
    {
        var driver = new RecordingConsoleDriver(120, 40);
        using var renderer = new NickConsoleExTuiRenderer(
            NullLogger<NickConsoleExTuiRenderer>.Instance, driverOverride: driver);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(Failure());

        await Assert.That(driver.Snapshot()).Contains(FailureLine)
            .Because("this handler is a near-copy of the AnsiPlain one and the two have "
                   + "drifted on purpose before; the composed cells are the only thing a "
                   + "user of HARBOR_TUI=nickconsoleex actually reads");
    }

    // ── Notifications ─────────────────────────────────────────────────────

    /// <summary>
    ///     NOT the same shape as the three above, and the difference is the
    ///     point. This renderer has no transcript: its handler toasts on
    ///     <c>CompactionCompletedEvent</c> and deliberately stays silent for
    ///     <c>CompactionStartedEvent</c>, because a toast per compaction start
    ///     is noise and a toast per completed compaction is a milestone. So its
    ///     missing arm is not an incomplete lifecycle family — it is a
    ///     <b>policy</b> about which milestones may interrupt the user, and the
    ///     question "does a degradation deserve a toast?" is answered by the
    ///     class's own contract rather than by symmetry with the siblings.
    /// </summary>
    [Test]
    public async Task Notifications_CompactionFailed_NotifiesTheDegradation()
    {
        var runner = new RecordingNotificationRunner();
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance, runner);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(Failure());

        await Assert.That(runner.Count).IsEqualTo(1)
            .Because("the class documents its scope as notifying when the agent "
                   + "\"finishes, errors, or runs into compaction\" — this renderer "
                   + "exists for a user who is NOT at the terminal, and an "
                   + "irreversible truncation is precisely what they cannot afford "
                   + "to discover by looking away");
        await Assert.That(string.Join(' ', runner.Single.Arguments))
            .Contains("summarizer timed out")
            .Because("a toast that says only \"compaction failed\" leaves the user "
                   + "unable to tell a transient summarizer hiccup from the session "
                   + "losing its history — the error text is the whole payload");
    }

    [Test]
    public async Task Notifications_CompactionFailed_TitleIsNotTheSuccessTitle()
    {
        var runner = new RecordingNotificationRunner();
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance, runner);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(
            new CompactionCompletedEvent("s1", "summary", 3, 100, TimeSpan.FromSeconds(1)));
        string successNotice = string.Join(' ', runner.Single.Arguments);

        var second = new RecordingNotificationRunner();
        using var renderer2 = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance, second);
        await renderer2.InitializeAsync();
        await renderer2.RenderAsync(Failure());
        string failureNotice = string.Join(' ', second.Single.Arguments);

        // The whole argv, not Arguments[0]: notify-send takes [title, body],
        // osascript takes one -e script, msg takes "*" "/TIME:10" "title\nbody".
        // The title's position is per-platform; its being different is not.
        await Assert.That(failureNotice).IsNotEqualTo(successNotice)
            .Because("both arms land in the same notification channel seconds apart; "
                   + "a shared title is how a user is told their context was cut using "
                   + "the toast that means it was pruned");
    }

    [Test]
    public async Task Notifications_CompactionStarted_StaysSilent()
    {
        // Green today, pinned so the fix above cannot be read as licence to toast
        // on every lifecycle member. The started arm's absence is the deliberate
        // half of this renderer's policy and the failure arm's presence is not a
        // precedent against it.
        var runner = new RecordingNotificationRunner();
        using var renderer = new NotificationTuiRenderer(
            NullLogger<NotificationTuiRenderer>.Instance, runner);
        await renderer.InitializeAsync();

        await renderer.RenderAsync(new CompactionStartedEvent("s1"));

        await Assert.That(runner.Count).IsEqualTo(0)
            .Because("compaction starts repeatedly through a long run; toasting each "
                   + "one is the noise this renderer's completed-only handler exists "
                   + "to avoid");
    }
}