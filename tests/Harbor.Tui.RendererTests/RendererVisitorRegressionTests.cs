namespace Harbor.Tui.RendererTests;

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.Notifications;
using Harbor.Tui.RendererTests.Support;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Harbor.TestKit;

/// <summary>
///     Visitor-registry regression for issue #185: the per-renderer
///     <c>switch (AgentEvent)</c> in AnsiPlain / Notifications moved into
///     registered <c>IAgentEventHandler</c>s. These tests pin the observable
///     behavior — handled events still paint, unhandled events stay silent,
///     and no event type throws on either renderer. (A third arm, over the
///     vendored NickConsoleEx backend, left with that backend.)
/// </summary>
/// <remarks>
///     <para>
///         <b>#840 removed <c>CompactionFailedEvent</c> from
///         <see cref="AnsiPlain_UnhandledEvents_WriteNothing"/>.</b> That line
///         pinned the bug: the assertion was that this renderer writes the EMPTY
///         STRING for a compaction that fell back to irreversible truncation. It
///         was a #185 characterization — "the registry refactor did not change
///         which events reach a handler" — and it was read as a decision, which
///         is what let the silence survive. Unhandled events are still silent;
///         this one is simply not unhandled any more.
///     </para>
///     <para>
///         <c>CompactionFailedEvent</c> is still in
///         <see cref="FullSweep"/> below: the sweep's job is "no event type
///         throws on any renderer", which the new arm has to keep true.
///     </para>
/// </remarks>
public class RendererVisitorRegressionTests
{
    private static AssistantMessage Partial(string id) => AssistantMessage.Empty("s1", id);

    private static JsonElement Args() =>
        JsonSerializer.SerializeToElement(
            new ToolPathLimitArgs("README.md", 10),
            TestJsonContext.Default.ToolPathLimitArgs);

    private static IEnumerable<AgentEvent> FullSweep()
    {
        var partial = Partial("sweep");
        yield return TestEvents.AgentStart();
        yield return TestEvents.TurnStart();
        yield return TestEvents.MessageStart(partial);
        yield return TestEvents.TextDeltaUpdate("0", "hi", partial);
        yield return TestEvents.ThinkingDeltaUpdate("0", "hmm", partial);
        yield return TestEvents.ToolCallDeltaUpdate("0", partial: partial);
        yield return TestEvents.MessageEnd(partial);
        yield return new ToolExecutionStartEvent("tc_1", "read", Args());
        yield return TestEvents.ToolExecutionUpdate("tc_1", "partial");
        yield return TestEvents.ToolExecutionEnd("tc_1", ToolResult.Success("ok"));
        yield return TestEvents.ToolExecutionEnd("tc_2", ToolResult.Error("boom"), true);
        yield return TestEvents.TurnEnd(partial);
        yield return TestEvents.CompactionStarted();
        yield return TestEvents.CompactionCompleted(prunedMessageCount: 3, tokensSaved: 100);
        yield return TestEvents.CompactionFailed(error: "nope");
        yield return TestEvents.SessionChanged();
        yield return TestEvents.PluginBlocked("p");
        yield return TestEvents.AgentError();
        yield return TestEvents.AgentEnd();
    }

    [Test]
    public async Task AnsiPlain_UnhandledEvents_WriteNothing()
    {
        using var writer = new StringWriter();
        var renderer = new PlainTuiRenderer(writer, new UiStore());
        try
        {
            await renderer.InitializeAsync();
            await renderer.RenderAsync(TestEvents.TurnStart());
            await renderer.RenderAsync(TestEvents.TurnEnd(Partial("t")));
            await renderer.RenderAsync(TestEvents.ToolExecutionUpdate("tc_1", "partial"));
            await renderer.RenderAsync(TestEvents.SessionChanged());
            await renderer.RenderAsync(TestEvents.PluginBlocked("p"));
        }
        finally
        {
            renderer.Dispose();
        }

        await Assert.That(writer.ToString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AnsiPlain_HandledEvents_ProduceLiveMarkers()
    {
        using var writer = new StringWriter();
        var renderer = new PlainTuiRenderer(writer, new UiStore());
        try
        {
            await renderer.InitializeAsync();
            var partial = Partial("m");
            await renderer.RenderAsync(TestEvents.MessageStart(partial));
            await renderer.RenderAsync(TestEvents.AgentError());
        }
        finally
        {
            renderer.Dispose();
        }

        string output = writer.ToString();
        await Assert.That(output).Contains("[assistant] ");
        await Assert.That(output).Contains("[error] boom");
    }

    [Test]
    public async Task AnsiPlain_FullEventSweep_DoesNotThrow()
    {
        using var writer = new StringWriter();
        var renderer = new PlainTuiRenderer(writer, new UiStore());
        try
        {
            await renderer.InitializeAsync();
            foreach (var evt in FullSweep())
            {
                await renderer.RenderAsync(evt);
            }
        }
        finally
        {
            renderer.Dispose();
        }

        await Assert.That(writer.ToString()).IsNotEmpty();
    }

    [Test]
    public async Task Notifications_FullEventSweep_DoesNotThrow()
    {
        // #665: the runner is injected, so this sweep no longer tries to pop real
        // toasts on whatever machine runs the suite. It used to shell out to
        // notify-send / osascript / msg for real.
        var runner = new RecordingNotificationRunner();
        var renderer = new NotificationTuiRenderer(NullLogger<NotificationTuiRenderer>.Instance, runner);
        try
        {
            await renderer.InitializeAsync();
            foreach (var evt in FullSweep())
            {
                await renderer.RenderAsync(evt);
            }
        }
        finally
        {
            renderer.Dispose();
        }

        // The tautology that used to stand here (`Assert.That(true).IsTrue()`)
        // asserted nothing (TUnit's TUnitAssertions0005 says so) and the real
        // assertion is the one below it: the sweep has to have reached the
        // notification runner.
        await Assert.That(runner.Count).IsGreaterThan(0)
            .Because("a sweep that throws nothing because it notified nothing is not a "
                   + "regression test of the notification path — the three events this "
                   + "renderer maps to a notification must all reach the runner");
    }
}
