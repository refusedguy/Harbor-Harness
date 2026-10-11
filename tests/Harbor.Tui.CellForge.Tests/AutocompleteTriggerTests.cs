using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Contract for <see cref="AutocompleteTriggerScan" /> (issue #1172, slice 1):
/// unified trigger-scan to reference|command|directory modes, 50ms anchor
/// tracking and keymap isolation while open, over <see cref="PromptBuffer" />.
/// </summary>
public class AutocompleteTriggerTests
{
    [Test]
    public async Task Scan_Command_AtLineStart()
    {
        bool found = AutocompleteTriggerScan.TryScan("/help".AsSpan(), 5, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Command);
        await Assert.That(trigger.AnchorIndex).IsEqualTo(0);
        await Assert.That(trigger.ExtractQuery("/help".AsSpan())).IsEqualTo("help");
    }

    [Test]
    public async Task Scan_Command_AfterSpace()
    {
        const string text = "run /help";
        bool found = AutocompleteTriggerScan.TryScan(text.AsSpan(), text.Length, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Command);
        await Assert.That(trigger.AnchorIndex).IsEqualTo(4);
    }

    [Test]
    public async Task Scan_BareSlash_OpensCommand_WithEmptyQuery()
    {
        bool found = AutocompleteTriggerScan.TryScan("/".AsSpan(), 1, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Command);
        await Assert.That(trigger.QueryLength).IsEqualTo(0);
    }

    [Test]
    public async Task Scan_MidWordSlash_IsDirectory_NotCommand()
    {
        bool found = AutocompleteTriggerScan.TryScan("a/b".AsSpan(), 3, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Directory);
        await Assert.That(trigger.AnchorIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Scan_Reference_Basic()
    {
        const string text = "see @file";
        bool found = AutocompleteTriggerScan.TryScan(text.AsSpan(), text.Length, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Reference);
        await Assert.That(trigger.AnchorIndex).IsEqualTo(4);
        await Assert.That(trigger.ExtractQuery(text.AsSpan())).IsEqualTo("file");
    }

    [Test]
    public async Task Scan_Reference_KeepsDottedName()
    {
        const string text = "@foo.ts";
        bool found = AutocompleteTriggerScan.TryScan(text.AsSpan(), text.Length, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Reference);
        await Assert.That(trigger.ExtractQuery(text.AsSpan())).IsEqualTo("foo.ts");
    }

    [Test]
    public async Task Scan_EmailLike_AtSign_StaysClosed()
    {
        bool found = AutocompleteTriggerScan.TryScan("a@b".AsSpan(), 3, out AutocompleteTrigger _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Scan_Directory_DotSlash()
    {
        const string text = "./src";
        bool found = AutocompleteTriggerScan.TryScan(text.AsSpan(), text.Length, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Directory);
        await Assert.That(trigger.AnchorIndex).IsEqualTo(0);
    }

    [Test]
    public async Task Scan_Directory_NestedPath()
    {
        const string text = "cd dir/sub";
        bool found = AutocompleteTriggerScan.TryScan(text.AsSpan(), text.Length, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Directory);
        await Assert.That(trigger.AnchorIndex).IsEqualTo(3);
    }

    [Test]
    public async Task Scan_PlainText_StaysClosed()
    {
        bool found = AutocompleteTriggerScan.TryScan("hello world".AsSpan(), 11, out AutocompleteTrigger _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Scan_CaretMidToken_SeesOnlyLeftPart()
    {
        const string text = "/help me";
        bool found = AutocompleteTriggerScan.TryScan(text.AsSpan(), 3, out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Command);
        await Assert.That(trigger.ExtractQuery(text.AsSpan())).IsEqualTo("he");
    }

    [Test]
    public async Task Anchor_Rescan_Throttled_To50ms()
    {
        await Assert.That(AutocompleteTriggerScan.ShouldRescan(100, 149)).IsFalse();
        await Assert.That(AutocompleteTriggerScan.ShouldRescan(100, 150)).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldRescan(100, 200)).IsTrue();
    }

    [Test]
    public async Task KeyIsolation_Consumes_PaletteKeys()
    {
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Up))).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Down))).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Enter))).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Escape))).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Tab))).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Backspace))).IsTrue();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(UiKey.ForChar('a'))).IsTrue();
    }

    [Test]
    public async Task KeyIsolation_Passes_Chords_And_FunctionKeys_Through()
    {
        var ctrlJ = new UiKey(UiKeyCode.Char, KeyModifierSet.Ctrl, 'j');
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(ctrlJ)).IsFalse();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.F1))).IsFalse();
        await Assert.That(AutocompleteTriggerScan.ShouldConsumeWhileOpen(new UiKey(UiKeyCode.Left))).IsFalse();
    }

    [Test]
    public async Task PromptBuffer_Extension_Scans_LiveContent()
    {
        var buffer = new PromptBuffer();
        _ = buffer.InsertText("/he");
        bool found = buffer.TryScanAutocomplete(out AutocompleteTrigger trigger);
        await Assert.That(found).IsTrue();
        await Assert.That(trigger.Mode).IsEqualTo(AutocompleteMode.Command);
        await Assert.That(trigger.ExtractQuery(buffer.AsSpan())).IsEqualTo("he");
    }
}
