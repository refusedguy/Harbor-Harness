// #1627 batch B (adapted): re-homed verbatim out of
// Harbor.Tui.CellForge/Chat/Widgets/AutocompleteTrigger.cs, which stays in chat
// (the trigger record, the trigger scan and the PromptBuffer extensions bind
// the chat key/buffer vocabulary, and KeyGateFamilyRules pins that file path).
// The enum itself binds nothing but BCL, so it moves beside the engine-hosted
// autocomplete filter family the way <see cref="AutocompleteItem"/>'s
// <c>Mode</c> parameter needs it.
namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Autocomplete trigger modes stolen from opencode's prompt layer
/// (<c>component/prompt/autocomplete.tsx</c> + <c>prompt/display.ts</c>
/// slash/mention trigger-index + <c>prompt/directory-completion.ts</c>):
/// <c>command</c> (<c>/</c>), <c>reference</c> (<c>@</c>) and
/// <c>directory</c> (a path token containing <c>/</c>).
/// </summary>
public enum AutocompleteMode : byte
{
    /// <summary>No trigger under the caret — the palette stays closed.</summary>
    None = 0,

    /// <summary><c>/</c> at a word start — slash-command completion.</summary>
    Command = 1,

    /// <summary><c>@</c> at a word start — file/context reference completion.</summary>
    Reference = 2,

    /// <summary>Path token containing <c>/</c> (<c>./x</c>, <c>~/x</c>, <c>dir/sub</c>).</summary>
    Directory = 3,
}
