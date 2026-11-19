using Harbor.Application.Agents;
using Harbor.TestKit;
using Harbor.Tui.CellForge;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.Tests;

/// <summary>
///     #3383: the previously dead <see cref="ActivityProjection"/> is the status
///     source of the canonical CellForge renderer. The assertions read the frame
///     painted into the terminal backend, not a view-model field, so the test
///     fails if the projection stops reaching the rendered status bar. Both
///     production and this test drive the projection through
///     <see cref="ActivityProjection.DescribeStatus"/>, so there is one mapping
///     from AgentEvent to status text.
/// </summary>
public class ActivityProjectionStatusTests
{
    [Test]
    public async Task AgentEvents_ChangeTheProjectedStatusTextOfTheFrame()
    {
        var activity = new ActivityProjection();
        var backend = new RecordingBackend();
        var renderer = new CellForgeTuiRenderer(
            NullLogger<CellForgeTuiRenderer>.Instance,
            backend,
            activityStatus: evt =>
            {
                activity.Apply(evt);
                return activity.DescribeStatus();
            });

        try
        {
            await renderer.InitializeAsync();

            backend.Reset();
            await renderer.RenderAsync(TestEvents.AgentStart());
            string runningFrame = backend.Text;
            await Assert.That(runningFrame).Contains("Run [running]");

            backend.Reset();
            await renderer.RenderAsync(TestEvents.AgentEnd());
            string doneFrame = backend.Text;
            await Assert.That(doneFrame).Contains("Run [done]");
            await Assert.That(runningFrame).DoesNotContain("Run [done]");
        }
        finally
        {
            renderer.Dispose();
        }
    }

    [Test]
    public async Task BeforeFirstAgent_StatusProjectionIsNull()
    {
        var activity = new ActivityProjection();
        await Assert.That(activity.DescribeStatus()).IsNull();

        activity.Apply(TestEvents.AgentStart());
        string? running = activity.DescribeStatus();
        await Assert.That(running).IsNotNull();
        await Assert.That(running!).Contains("Run [running]");
    }
}
