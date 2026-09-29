using Harbor.Abstractions.Models;
using Harbor.Tui.RazorConsole.Rendering;
using Harbor.Tui.SpectreTui.View;
using Harbor.Tui.Termina.Rendering;
using Harbor.Tui.TerminalGui.Rendering;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Tui.Tests;

/// <summary>
///     #556 — the <see cref="ChatRole" /> presentation policy used to be written
///     out four times (three of them byte-identical) and every copy ended in
///     <c>_ =&gt; "msg"</c>, so adding a role relabelled it in all four
///     renderers with no warning and no failing test. It now lives once, in
///     <see cref="ChatRolePresentation" />.
/// </summary>
/// <remarks>
///     <para>
///         C# refuses to check enum exhaustiveness here (a discard-free switch
///         over <see cref="ChatRole" /> is <c>CS8524</c>, not a total match), so
///         the guarantee is split in two: the runtime default arm
///         <em>throws</em> — a new role is loud, never <c>"msg"</c> — and these
///         tests walk <see cref="Enum.GetValues{TEnum}()" />, so the policy is
///         pinned as a table and a role added without a pinned row fails CI.
///     </para>
///     <para>
///         Deliberately not covered here: the desktop bubble label
///         (<c>ChatLineViewModel.RoleLabel</c>). That is a different surface
///         with a different vocabulary (<c>"user"</c>, not <c>"you"</c>), pinned
///         by another suite, and its default prints the real role name instead
///         of swallowing it — the quiet failure mode #556 is about.
///     </para>
/// </remarks>
public class ChatRolePresentationTests
{
    /// <summary>
    ///     The pinned policy. Adding a <see cref="ChatRole" /> means adding a
    ///     row here as well as an arm to the table — the count check below is
    ///     what makes forgetting the row a test failure.
    /// </summary>
    private static readonly Dictionary<ChatRole, (string Label, bool Markdown, ChatColorSlot Slot)> Pinned = new()
    {
        [ChatRole.User] = ("you", true, ChatColorSlot.User),
        [ChatRole.Assistant] = ("assistant", true, ChatColorSlot.Assistant),
        [ChatRole.Thinking] = ("thinking", false, ChatColorSlot.Muted),
        [ChatRole.Tool] = ("tool", false, ChatColorSlot.Tool),
        [ChatRole.ToolResult] = ("result", false, ChatColorSlot.Muted),
        [ChatRole.System] = ("system", true, ChatColorSlot.Muted),
        [ChatRole.Error] = ("error", false, ChatColorSlot.Danger)
    };

    private static (string Label, bool Markdown, ChatColorSlot Slot) Row(ChatRole role) =>
        Pinned.TryGetValue(role, out var row)
            ? row
            : throw new InvalidOperationException(
                $"{role} is not pinned in ChatRolePresentationTests — add it to the Pinned table.");

    [Test]
    public async Task EveryChatRoleMember_IsPinnedInThisFile()
    {
        ChatRole[] declared = Enum.GetValues<ChatRole>();

        await Assert.That(Pinned.Count).IsEqualTo(declared.Length)
            .Because("a new ChatRole must be added to the Pinned table in this file — that is the CI signal #556 asks for");

        foreach (ChatRole role in declared)
            await Assert.That(Pinned.ContainsKey(role)).IsTrue()
                .Because($"{role} has no row in the Pinned table");
    }

    [Test]
    public async Task Label_IsTotal_AndMatchesThePinnedTable()
    {
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
        {
            string label = ChatRolePresentation.Label(role);

            await Assert.That(label).IsNotEmpty().Because($"{role} must not render a blank header");
            await Assert.That(label).IsEqualTo(Row(role).Label).Because($"the {role} header label changed");
        }
    }

    [Test]
    public async Task Label_IsNeverTheOldSilentFallback()
    {
        // The bug: all four tables answered "msg" for anything they did not
        // know, so a new role was relabelled instead of reported.
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
            await Assert.That(ChatRolePresentation.Label(role)).IsNotEqualTo("msg")
                .Because($"{role} fell through to the silent \"msg\" default that #556 removed");
    }

    [Test]
    public async Task UsesMarkdown_IsTotal_AndMatchesThePinnedTable()
    {
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
            await Assert.That(ChatRolePresentation.UsesMarkdown(role))
                .IsEqualTo(Row(role).Markdown)
                .Because($"the markdown rule for {role} changed");
    }

    [Test]
    public async Task Slot_IsTotal_AndMatchesThePinnedTable()
    {
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
            await Assert.That(ChatRolePresentation.Slot(role))
                .IsEqualTo(Row(role).Slot)
                .Because($"the colour slot for {role} changed");
    }

    [Test]
    public async Task Describe_AgreesWithTheThreeAccessors()
    {
        // One implementation: the accessors must not be able to drift from the
        // tuple they delegate to.
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
        {
            (string label, bool markdown, ChatColorSlot slot) = ChatRolePresentation.Describe(role);

            await Assert.That(label).IsEqualTo(ChatRolePresentation.Label(role));
            await Assert.That(markdown).IsEqualTo(ChatRolePresentation.UsesMarkdown(role));
            await Assert.That(slot).IsEqualTo(ChatRolePresentation.Slot(role));
        }
    }

    [Test]
    public async Task AllBackends_DeriveTheirLabelFromTheSharedTable()
    {
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
        {
            string expected = ChatRolePresentation.Label(role);

            await Assert.That(RazorColorMapper.ToLabel(role)).IsEqualTo(expected).Because("RazorConsole");
            await Assert.That(TerminaColorMapper.ToLabel(role)).IsEqualTo(expected).Because("Termina");
            await Assert.That(TerminalGuiColorMapper.ToLabel(role)).IsEqualTo(expected).Because("Terminal.Gui");
        }
    }

    [Test]
    public async Task AllBackends_DeriveTheirMarkdownRuleFromTheSharedTable()
    {
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
        {
            bool expected = ChatRolePresentation.UsesMarkdown(role);

            await Assert.That(RazorColorMapper.SupportsMarkdown(role)).IsEqualTo(expected).Because("RazorConsole");
            await Assert.That(TerminaColorMapper.SupportsMarkdown(role)).IsEqualTo(expected).Because("Termina");
            await Assert.That(TerminalGuiColorMapper.SupportsMarkdown(role)).IsEqualTo(expected).Because("Terminal.Gui");
        }
    }

    [Test]
    public async Task AllBackends_PaintRolesThatShareASlotWithTheSameHue()
    {
        // The invariant the deleted "same hue" comments promised and did not
        // keep: Termina painted Thinking DarkGray while its ToolResult and
        // System were Gray, and SpectreTui painted the assistant band Aqua while
        // the assistant body was White. Roles sharing a slot must now resolve to
        // one colour per backend — which holds only if the backend palette table
        // is keyed on the slot rather than on the role.
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
        {
            ChatColorSlot slot = ChatRolePresentation.Slot(role);

            await Assert.That(RazorColorMapper.ToColor(role))
                .IsEqualTo(RazorColorMapper.ToColor(slot)).Because($"RazorConsole {role}");
            await Assert.That(TerminaColorMapper.ToColor(role))
                .IsEqualTo(TerminaColorMapper.ToColor(slot)).Because($"Termina {role}");
            await Assert.That(TerminalGuiColorMapper.ToColor(role))
                .IsEqualTo(TerminalGuiColorMapper.ToColor(slot)).Because($"Terminal.Gui {role}");
            await Assert.That(ChatMessageFormatter.ToColor(role))
                .IsEqualTo(ChatMessageFormatter.ToColor(slot)).Because($"SpectreTui {role}");
        }
    }

    [Test]
    public async Task MutedFamily_SharesOneHueInEveryBackend()
    {
        // Thinking, ToolResult and System are all Muted. This is the concrete
        // pair that used to drift inside a single backend.
        foreach (ChatRole role in new[] { ChatRole.Thinking, ChatRole.ToolResult, ChatRole.System })
        {
            await Assert.That(ChatRolePresentation.Slot(role)).IsEqualTo(ChatColorSlot.Muted).Because($"{role}");

            await Assert.That(RazorColorMapper.ToColor(role))
                .IsEqualTo(RazorColorMapper.ToColor(ChatRole.ToolResult)).Because($"RazorConsole {role}");
            await Assert.That(TerminaColorMapper.ToColor(role))
                .IsEqualTo(TerminaColorMapper.ToColor(ChatRole.ToolResult)).Because($"Termina {role}");
            await Assert.That(TerminalGuiColorMapper.ToColor(role))
                .IsEqualTo(TerminalGuiColorMapper.ToColor(ChatRole.ToolResult)).Because($"Terminal.Gui {role}");
            await Assert.That(ChatMessageFormatter.ToColor(role))
                .IsEqualTo(ChatMessageFormatter.ToColor(ChatRole.ToolResult)).Because($"SpectreTui {role}");
        }
    }

    [Test]
    public async Task RazorConsole_KeepsItalicOnThinkingOnly()
    {
        // The one deliberate per-backend decoration: RazorConsole italicises
        // thinking output. Pinned so a "unify everything" refactor has to decide
        // about it on purpose rather than by accident.
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.User)).IsEqualTo("green");
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.Assistant)).IsEqualTo("white");
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.Thinking)).IsEqualTo("grey italic");
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.Tool)).IsEqualTo("blue");
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.ToolResult)).IsEqualTo("grey");
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.System)).IsEqualTo("grey");
        await Assert.That(RazorColorMapper.ToMarkup(ChatRole.Error)).IsEqualTo("red");
    }

    [Test]
    public async Task RenderedHeaders_UseTheSharedLabelInEveryBackend()
    {
        // End to end: the label that actually reaches the screen, not just the
        // mapper. Razor and Termina return markup / ANSI strings, Terminal.Gui
        // plain text — all three embed the shared label verbatim.
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
        {
            string label = ChatRolePresentation.Label(role);

            await Assert.That(RazorMarkdownRenderer.RenderHeader(role)).Contains(label).Because($"RazorConsole {role}");
            await Assert.That(TerminaMarkdownRenderer.RenderHeader(role)).Contains(label).Because($"Termina {role}");
            await Assert.That(TerminalGuiMarkdownRenderer.RenderHeader(role)).Contains(label).Because($"Terminal.Gui {role}");
        }
    }

    [Test]
    public async Task RenderedHeaders_CarryTheRoleNameForEveryRole()
    {
        // The failure mode #556 described: a new role renders as "msg" in all
        // four backends. Terminal.Gui's header is bare text, so its whole
        // content must be the shared label between the two band rules.
        foreach (ChatRole role in Enum.GetValues<ChatRole>())
            await Assert.That(TerminalGuiMarkdownRenderer.RenderHeader(role))
                .IsEqualTo($"─ {ChatRolePresentation.Label(role)} ─")
                .Because($"Terminal.Gui painted {role} with something other than the shared label");
    }

    [Test]
    public async Task AnUnhandledRole_ThrowsInsteadOfBeingRelabelled()
    {
        // The "loud" half of the guarantee. ChatRole has no member outside the
        // pinned seven, so this is exactly what a future ChatRole member hits if
        // its author forgets the table — it must never come back as "msg".
        ChatRole imaginary = (ChatRole)byte.MaxValue;

        ArgumentOutOfRangeException fromTable =
            Assert.Throws<ArgumentOutOfRangeException>(() => ChatRolePresentation.Describe(imaginary));
        ArgumentOutOfRangeException fromLabel =
            Assert.Throws<ArgumentOutOfRangeException>(() => ChatRolePresentation.Label(imaginary));
        ArgumentOutOfRangeException fromSlot =
            Assert.Throws<ArgumentOutOfRangeException>(() => ChatRolePresentation.Slot(imaginary));
        ArgumentOutOfRangeException fromBackend =
            Assert.Throws<ArgumentOutOfRangeException>(() => RazorColorMapper.ToLabel(imaginary));

        await Assert.That(fromTable.Message.Contains("ChatRolePresentation.Describe", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fromLabel.Message.Contains("ChatRolePresentationTests.Pinned", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fromSlot.Message.Contains("ChatRolePresentationTests.Pinned", StringComparison.Ordinal)).IsTrue();
        await Assert.That(fromBackend.Message.Contains("ChatRolePresentation.Describe", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AnUnhandledSlot_ThrowsInEveryBackendPalette()
    {
        // The same guarantee one level down: no backend answers an unknown slot
        // with a colour, because "some colour" is how a role silently repaints.
        ChatColorSlot imaginary = (ChatColorSlot)byte.MaxValue;

        ArgumentOutOfRangeException razor =
            Assert.Throws<ArgumentOutOfRangeException>(() => RazorColorMapper.ToColor(imaginary));
        ArgumentOutOfRangeException termina =
            Assert.Throws<ArgumentOutOfRangeException>(() => TerminaColorMapper.ToColor(imaginary));
        ArgumentOutOfRangeException terminalGui =
            Assert.Throws<ArgumentOutOfRangeException>(() => TerminalGuiColorMapper.ToColor(imaginary));
        ArgumentOutOfRangeException spectre =
            Assert.Throws<ArgumentOutOfRangeException>(() => ChatMessageFormatter.ToColor(imaginary));

        foreach (ArgumentOutOfRangeException ex in new[] { razor, termina, terminalGui, spectre })
            await Assert.That(ex.Message.Contains("ChatColorSlot", StringComparison.Ordinal)).IsTrue();
    }
}
