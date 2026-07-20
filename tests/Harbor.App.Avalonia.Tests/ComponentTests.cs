using Harbor.App.Avalonia.Views.Components;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Unit tests for the reusable React-style components extracted in
///     Task R28: <see cref="StatusBadge"/>, <see cref="ChatBubble"/>,
///     <see cref="SessionRow"/>. These verify that the bindable
///     properties default correctly and that setters actually update the
///     underlying styled-property values (so {Binding} from AXAML will
///     re-render).
/// </summary>
/// <remarks>
///     Headless: no Avalonia application is spun up. The controls are
///     instantiated directly and their property values are read back.
///     This catches regressions like accidentally renaming a
///     StyledProperty (which would silently break all bindings) or
///     removing a default (which would change the initial render).
/// </remarks>
public class ComponentTests
{
    // ── StatusBadge ─────────────────────────────────────────────────

    [Test]
    public async Task StatusBadge_Default_StatusText_IsEmpty()
    {
        var badge = new StatusBadge();
        await Assert.That(badge.StatusText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task StatusBadge_Default_BrushKey_IsIdle()
    {
        var badge = new StatusBadge();
        await Assert.That(badge.BrushKey).IsEqualTo("StatusIdleBrush");
    }

    [Test]
    public async Task StatusBadge_Default_ShowDot_IsTrue()
    {
        var badge = new StatusBadge();
        await Assert.That(badge.ShowDot).IsTrue();
    }

    [Test]
    public async Task StatusBadge_SetStatusText_UpdatesProperty()
    {
        var badge = new StatusBadge { StatusText = "running" };
        await Assert.That(badge.StatusText).IsEqualTo("running");
    }

    [Test]
    public async Task StatusBadge_SetBrushKey_UpdatesProperty()
    {
        var badge = new StatusBadge { BrushKey = "StatusErrorBrush" };
        await Assert.That(badge.BrushKey).IsEqualTo("StatusErrorBrush");
    }

    [Test]
    public async Task StatusBadge_SetShowDot_False_UpdatesProperty()
    {
        var badge = new StatusBadge { ShowDot = false };
        await Assert.That(badge.ShowDot).IsFalse();
    }

    // ── ChatBubble ──────────────────────────────────────────────────

    [Test]
    public async Task ChatBubble_Default_RoleLabel_IsUser()
    {
        var bubble = new ChatBubble();
        await Assert.That(bubble.RoleLabel).IsEqualTo("user");
    }

    [Test]
    public async Task ChatBubble_Default_Text_IsEmpty()
    {
        var bubble = new ChatBubble();
        await Assert.That(bubble.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ChatBubble_Default_BrushKey_IsChatUserBrush()
    {
        var bubble = new ChatBubble();
        await Assert.That(bubble.BrushKey).IsEqualTo("ChatUserBrush");
    }

    [Test]
    public async Task ChatBubble_Default_Timestamp_IsNull()
    {
        var bubble = new ChatBubble();
        await Assert.That(bubble.Timestamp).IsNull();
    }

    [Test]
    public async Task ChatBubble_Default_IsCompact_IsFalse()
    {
        var bubble = new ChatBubble();
        await Assert.That(bubble.IsCompact).IsFalse();
    }

    [Test]
    public async Task ChatBubble_SetAll_Properties_Update()
    {
        var bubble = new ChatBubble
        {
            RoleLabel = "assistant",
            Text = "Hello, world!",
            BrushKey = "ChatAssistantBrush",
            Timestamp = "2m ago",
            IsCompact = true
        };
        await Assert.That(bubble.RoleLabel).IsEqualTo("assistant");
        await Assert.That(bubble.Text).IsEqualTo("Hello, world!");
        await Assert.That(bubble.BrushKey).IsEqualTo("ChatAssistantBrush");
        await Assert.That(bubble.Timestamp).IsEqualTo("2m ago");
        await Assert.That(bubble.IsCompact).IsTrue();
    }

    // ── SessionRow ──────────────────────────────────────────────────

    [Test]
    public async Task SessionRow_Default_Title_IsEmpty()
    {
        var row = new SessionRow();
        await Assert.That(row.Title).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SessionRow_Default_Subtitle_IsEmpty()
    {
        var row = new SessionRow();
        await Assert.That(row.Subtitle).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SessionRow_Default_RelativeTime_IsEmpty()
    {
        var row = new SessionRow();
        await Assert.That(row.RelativeTime).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SessionRow_Default_MessageCount_IsZero()
    {
        var row = new SessionRow();
        await Assert.That(row.MessageCount).IsEqualTo(0);
    }

    [Test]
    public async Task SessionRow_Default_StatusColorKey_IsOverlay0()
    {
        var row = new SessionRow();
        await Assert.That(row.StatusColorKey).IsEqualTo("MochaOverlay0");
    }

    [Test]
    public async Task SessionRow_Default_IsDirty_IsFalse()
    {
        var row = new SessionRow();
        await Assert.That(row.IsDirty).IsFalse();
    }

    [Test]
    public async Task SessionRow_Default_IsActive_IsFalse()
    {
        var row = new SessionRow();
        await Assert.That(row.IsActive).IsFalse();
    }

    [Test]
    public async Task SessionRow_SetAll_Properties_Update()
    {
        var row = new SessionRow
        {
            Title = "main chat",
            Subtitle = "code - gpt-4o",
            RelativeTime = "5m ago",
            MessageCount = 12,
            StatusColorKey = "MochaYellow",
            IsDirty = true,
            IsActive = true
        };
        await Assert.That(row.Title).IsEqualTo("main chat");
        await Assert.That(row.Subtitle).IsEqualTo("code - gpt-4o");
        await Assert.That(row.RelativeTime).IsEqualTo("5m ago");
        await Assert.That(row.MessageCount).IsEqualTo(12);
        await Assert.That(row.StatusColorKey).IsEqualTo("MochaYellow");
        await Assert.That(row.IsDirty).IsTrue();
        await Assert.That(row.IsActive).IsTrue();
    }
}
