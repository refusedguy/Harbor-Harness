namespace Harbor.Tui.RendererTests;

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.NickConsoleEx;
using Harbor.Tui.Notifications;
using Harbor.Tui.RendererTests.Support;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Visitor-registry regression for issue #185: the per-renderer
///     <c>switch (AgentEvent)</c> in AnsiPlain / NickConsoleEx / Notifications
///     moved into registered <c>IAgentEventHandler</c>s. These tests pin the
///     observable behavior — handled events still paint, unhandled events stay
///     silent, and no event type throws on any of the three renderers.
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
        JsonSerializer.SerializeToElement(new { path = "README.md", limit = 10 });

    private static IEnumerable<AgentEvent> FullSweep()
    {
        var partial = Partial("sweep");
        yield return new AgentStartEvent("s1", []);
        yield return new TurnStartEvent(1);
        yield return new MessageStartEvent(partial);
        yield return new MessageUpdateEvent(new TextDeltaEvent("0", "hi"), partial);
        yield return new MessageUpdateEvent(new ThinkingDeltaEvent("0", "hmm"), partial);
        yield return new MessageUpdateEvent(new ToolCallDeltaEvent("0", "{}"), partial);
        yield return new MessageEndEvent(partial);
        yield return new ToolExecutionStartEvent("tc_1", "read", Args());
        yield return new ToolExecutionUpdateEvent("tc_1", "partial");
        yield return new ToolExecutionEndEvent("tc_1", ToolResult.Success("ok"), IsError: false);
        yield return new ToolExecutionEndEvent("tc_2", ToolResult.Error("boom"), IsError: true);
        yield return new TurnEndEvent(partial, []);
        yield return new CompactionStartedEvent("s1");
        yield return new CompactionCompletedEvent("s1", "summary", 3, 100, TimeSpan.FromSeconds(1));
        yield return new CompactionFailedEvent("s1", "nope");
        yield return new SessionChangedEvent("s1");
        yield return new PluginBlockedEvent("p", "timeout", "detail");
        yield return new AgentErrorEvent("boom");
        yield return new AgentEndEvent([]);
    }

    [Test]
    public async Task AnsiPlain_UnhandledEvents_WriteNothing()
    {
        using var writer = new StringWriter();
        var renderer = new PlainTuiRenderer(writer, new UiStore());
        try
        {
            await renderer.InitializeAsync();
            await renderer.RenderAsync(new TurnStartEvent(1));
            await renderer.RenderAsync(new TurnEndEvent(Partial("t"), []));
            await renderer.RenderAsync(new ToolExecutionUpdateEvent("tc_1", "partial"));
            await renderer.RenderAsync(new SessionChangedEvent("s1"));
            await renderer.RenderAsync(new PluginBlockedEvent("p", "timeout", "detail"));
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
            await renderer.RenderAsync(new MessageStartEvent(partial));
            await renderer.RenderAsync(new AgentErrorEvent("boom"));
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
    public async Task NickConsoleEx_FullEventSweep_DoesNotThrow()
    {
        var driver = new RecordingConsoleDriver(120, 40);
        var renderer = new NickConsoleExTuiRenderer(
            NullLogger<NickConsoleExTuiRenderer>.Instance, driverOverride: driver);
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

        await Assert.That(driver.Snapshot()).IsNotEmpty();
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

        await Assert.That(true).IsTrue();

        await Assert.That(runner.Count).IsGreaterThan(0)
            .Because("a sweep that throws nothing because it notified nothing is not a "
                   + "regression test of the notification path — the three events this "
                   + "renderer maps to a notification must all reach the runner");
    }
}
