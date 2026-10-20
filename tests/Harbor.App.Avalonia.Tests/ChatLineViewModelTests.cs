using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
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

    // #1770: the role→brush copies differed only in the role, the sample text
    // and the expected key — the assert never changed. All three are the table
    // now, and every row keeps its original test name as the display name.
    [Test]
    [Arguments(ChatRole.User, "hi", "ChatUserBrush", DisplayName = "RoleBrushKey_User_ReturnsChatUserBrush")]
    [Arguments(ChatRole.Assistant, "hello", "ChatAssistantBrush", DisplayName = "RoleBrushKey_Assistant_ReturnsChatAssistantBrush")]
    [Arguments(ChatRole.Thinking, "hmm", "ChatThinkingBrush", DisplayName = "RoleBrushKey_Thinking_ReturnsChatThinkingBrush")]
    [Arguments(ChatRole.Tool, "executing", "ChatToolBrush", DisplayName = "RoleBrushKey_Tool_ReturnsChatToolBrush")]
    [Arguments(ChatRole.ToolResult, "done", "ChatToolResultBrush", DisplayName = "RoleBrushKey_ToolResult_ReturnsChatToolResultBrush")]
    [Arguments(ChatRole.System, "note", "ChatSystemBrush", DisplayName = "RoleBrushKey_System_ReturnsChatSystemBrush")]
    [Arguments(ChatRole.Error, "oops", "ChatErrorBrush", DisplayName = "RoleBrushKey_Error_ReturnsChatErrorBrush")]
    public async Task RoleBrushKey_ReturnsRoleBrushKey(ChatRole role, string text, string expectedBrush)
    {
        var vm = new ChatLineVm(role, text);
        await Assert.That(vm.RoleBrushKey).IsEqualTo(expectedBrush);
    }

    [Test]
    public async Task BrushKey_LegacyAlias_MatchesRoleBrushKey()
    {
        var vm = new ChatLineVm(ChatRole.User, "hi");
        await Assert.That(vm.BrushKey).IsEqualTo(vm.RoleBrushKey);
    }

    // ── RoleLabel ──────────────────────────────────────────────────

    [Test]
    [Arguments(ChatRole.User, "hi", "user", DisplayName = "RoleLabel_User_ReturnsUser")]
    [Arguments(ChatRole.Assistant, "hello", "assistant", DisplayName = "RoleLabel_Assistant_ReturnsAssistant")]
    [Arguments(ChatRole.Thinking, "hmm", "thinking", DisplayName = "RoleLabel_Thinking_ReturnsThinking")]
    [Arguments(ChatRole.Tool, "executing", "tool", DisplayName = "RoleLabel_Tool_ReturnsTool")]
    [Arguments(ChatRole.ToolResult, "done", "tool-result", DisplayName = "RoleLabel_ToolResult_ReturnsToolResult")]
    [Arguments(ChatRole.System, "note", "system", DisplayName = "RoleLabel_System_ReturnsSystem")]
    [Arguments(ChatRole.Error, "oops", "error", DisplayName = "RoleLabel_Error_ReturnsError")]
    public async Task RoleLabel_ReturnsRoleLabel(ChatRole role, string text, string expectedLabel)
    {
        var vm = new ChatLineVm(role, text);
        await Assert.That(vm.RoleLabel).IsEqualTo(expectedLabel);
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
