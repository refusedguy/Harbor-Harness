using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.CellForge.Tests.Pilot;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>Self-tests for the <see cref="CellForgePilot" /> headless driver (#1183).</summary>
public sealed class CellForgePilotTests
{
    [Test]
    public async Task Press_TypedChars_LandInComposer()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        await pilot.TypeText("привет");

        await Assert.That(pilot.ComposerText()).IsEqualTo("привет");
        await Assert.That(pilot.InputVm.Text).IsEqualTo("привет");
    }

    [Test]
    public async Task Press_Backspace_EditsComposer()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        await pilot.TypeText("abc");
        await pilot.PressBackspace();

        await Assert.That(pilot.ComposerText()).IsEqualTo("ab");
    }

    [Test]
    public async Task Press_Enter_Submits_AndClearsComposer()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        await pilot.TypeText("hello");
        await pilot.PressEnter();

        await Assert.That(pilot.ComposerText()).IsEqualTo(string.Empty);
        await Assert.That(pilot.InputVm.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task WaitForIdle_DrainsPump_AndReportsIdle()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();

        await Assert.That(await pilot.WaitForIdleAsync()).IsTrue();
        await Assert.That(pilot.Renderer.HasPendingWork).IsFalse();
    }

    [Test]
    public async Task MessageHook_CapturesPublishedEvents_InOrder()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        var seen = new List<AgentEvent>();
        using var _ = pilot.Hook((e, _) =>
        {
            seen.Add(e);
            return ValueTask.CompletedTask;
        });

        var start = new AgentStartEvent("s1", Array.Empty<AgentMessage>());
        var end = new AgentEndEvent(Array.Empty<AgentMessage>());
        await pilot.PublishAsync(start);
        await pilot.PublishAsync(end);

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[0]).IsTypeOf<AgentStartEvent>();
        await Assert.That(seen[1]).IsTypeOf<AgentEndEvent>();
        await Assert.That(pilot.SeenEvents.Count).IsEqualTo(2);
    }

    [Test]
    public async Task WaitForEvent_ResolvesAlreadySeen_WithoutDelay()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();
        await pilot.PublishAsync(new AgentEndEvent(Array.Empty<AgentMessage>()));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var seen = await pilot.WaitForEventAsync<AgentEndEvent>();
        sw.Stop();

        await Assert.That(seen).IsNotNull();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task WaitForEvent_MissingEvent_TimesOut()
    {
        await using var pilot = await CellForgePilot.LaunchAsync();

        bool timedOut = false;
        try
        {
            await pilot.WaitForEventAsync<AgentErrorEvent>(timeout: TimeSpan.FromMilliseconds(50));
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        await Assert.That(timedOut).IsTrue();
    }
}
