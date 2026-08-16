using Harbor.Ui.Framework.State;
using Harbor.Ui.Framework.ViewModels;
using ChatLineVm = Harbor.Ui.Framework.ViewModels.ChatLineViewModel;
using ToolCallVm = Harbor.Ui.Framework.ViewModels.ToolCallViewModel;
namespace Harbor.App.Avalonia.Tests;
/// <summary>
///     Unit tests for the platform-agnostic <see cref="ChatLineVm" />
///     record. Verifies that the role-to-brush-key / role-to-label /
///     timestamp-text / preview projections stay stable across UI
///     frameworks (the same VM is bound from Avalonia, WPF, MAUI, Blazor).
/// </summary>
/// <remarks>
///     These tests guard against accidental drift — e.g. someone renaming
///     <c>ChatUserBrush</c> in the AXAML resource dictionary without
///     updating the VM, or adding a new <c>ChatRole</c> value without
///     updating the switch expressions.
/// </remarks>
public class ChatLineVmTests
{
    // ── RoleBrushKey ───────────────────────────────────────────────

    [Test]
    public async Task RoleBrushKey_User_ReturnsChatUserBrush()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatUserBrush");
    }

    [Test]
    public async Task RoleBrushKey_Assistant_ReturnsChatAssistantBrush()
    {
        var vm = new ChatLineVm(ChatRole.Assistant, "hello");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatAssistantBrush");
    }

    [Test]
    public async Task RoleBrushKey_Thinking_ReturnsChatThinkingBrush()
    {
        var vm = new ChatLineVm(ChatRole.Thinking, "hmm");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatThinkingBrush");
    }

    [Test]
    public async Task RoleBrushKey_Tool_ReturnsChatToolBrush()
    {
        var vm = new ChatLineVm(ChatRole.Tool, "executing");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatToolBrush");
    }

    [Test]
    public async Task RoleBrushKey_ToolResult_ReturnsChatToolResultBrush()
    {
        var vm = new ChatLineVm(ChatRole.ToolResult, "done");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatToolResultBrush");
    }

    [Test]
    public async Task RoleBrushKey_System_ReturnsChatSystemBrush()
    {
        var vm = new ChatLineVm(ChatRole.System, "note");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatSystemBrush");
    }

    [Test]
    public async Task RoleBrushKey_Error_ReturnsChatErrorBrush()
    {
        var vm = new ChatLineVm(ChatRole.Error, "oops");
        await Assert.That(vm.RoleBrushKey).IsEqualTo("ChatErrorBrush");
    }

    [Test]
    public async Task BrushKey_LegacyAlias_MatchesRoleBrushKey()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi");
        await Assert.That(vm.BrushKey).IsEqualTo(vm.RoleBrushKey);
    }

    // ── RoleLabel ──────────────────────────────────────────────────

    [Test]
    public async Task RoleLabel_User_ReturnsUser()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi");
        await Assert.That(vm.RoleLabel).IsEqualTo("user");
    }

    [Test]
    public async Task RoleLabel_Assistant_ReturnsAssistant()
    {
        var vm = new ChatLineVm(ChatRole.Assistant, "hello");
        await Assert.That(vm.RoleLabel).IsEqualTo("assistant");
    }

    [Test]
    public async Task RoleLabel_Thinking_ReturnsThinking()
    {
        var vm = new ChatLineVm(ChatRole.Thinking, "hmm");
        await Assert.That(vm.RoleLabel).IsEqualTo("thinking");
    }

    [Test]
    public async Task RoleLabel_Tool_ReturnsTool()
    {
        var vm = new ChatLineVm(ChatRole.Tool, "executing");
        await Assert.That(vm.RoleLabel).IsEqualTo("tool");
    }

    [Test]
    public async Task RoleLabel_ToolResult_ReturnsToolResult()
    {
        var vm = new ChatLineVm(ChatRole.ToolResult, "done");
        await Assert.That(vm.RoleLabel).IsEqualTo("tool-result");
    }

    [Test]
    public async Task RoleLabel_System_ReturnsSystem()
    {
        var vm = new ChatLineVm(ChatRole.System, "note");
        await Assert.That(vm.RoleLabel).IsEqualTo("system");
    }

    [Test]
    public async Task RoleLabel_Error_ReturnsError()
    {
        var vm = new ChatLineVm(ChatRole.Error, "oops");
        await Assert.That(vm.RoleLabel).IsEqualTo("error");
    }

    // ── TimestampText ──────────────────────────────────────────────

    [Test]
    public async Task TimestampText_Null_ReturnsEmpty()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi");
        await Assert.That(vm.TimestampText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TimestampText_JustNow_ReturnsJustNow()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi")
        {
            TimestampUtc = DateTime.UtcNow.AddSeconds(5)
        };
        await Assert.That(vm.TimestampText).IsEqualTo("just now");
    }

    [Test]
    public async Task TimestampText_FiveMinutesAgo_Returns5mAgo()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi")
        {
            TimestampUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        await Assert.That(vm.TimestampText).IsEqualTo("5m ago");
    }

    // ── Preview ────────────────────────────────────────────────────

    [Test]
    public async Task Preview_ShortText_ReturnsFullText()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi");
        await Assert.That(vm.Preview).IsEqualTo("hi");
    }

    [Test]
    public async Task Preview_LongText_TruncatesTo77CharsPlusEllipsis()
    {
        string longText = new('a', 100);
        var vm = new ChatLineVm(ChatRole.User, longText);
        await Assert.That(vm.Preview.Length).IsEqualTo(80);
        await Assert.That(vm.Preview).EndsWith("...");
    }

    [Test]
    public async Task Preview_Exactly80Chars_ReturnsFullText()
    {
        string text = new('a', 80);
        var vm = new ChatLineVm(ChatRole.User, text);
        await Assert.That(vm.Preview).IsEqualTo(text);
    }
}
