// #3432: declarative key contexts for shared-layer widgets. Same public shape
// as the engine's Harbor.Tui.CellForge.Input primitive, but this copy is
// implemented here because the shared layer cannot reference the engine leaf
// (CellForgeGraphRules). The owning view gives the opaque action ids meaning.

using System.Text;

namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>A gesture-to-action entry. A null action id swallows the key.</summary>
public readonly struct ContextBinding
{
    public ContextBinding(KeyGesture gesture, string? actionId)
    {
        Gesture = gesture;
        ActionId = actionId;
    }

    public KeyGesture Gesture { get; }

    public string? ActionId { get; }

    public bool IsNoAction => ActionId is null;

    public static ContextBinding Action(KeyGesture gesture, string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return new ContextBinding(gesture, actionId);
    }

    public static ContextBinding NoAction(KeyGesture gesture) => new(gesture, null);
}

/// <summary>A named key or exact character, with the exact modifier set required.</summary>
public readonly record struct KeyGesture(KeyCode Code, KeyModifiers Mods, Rune Symbol)
{
    public static KeyGesture ForKey(KeyCode code, KeyModifiers mods = KeyModifiers.None) =>
        new(code, mods, default);

    public static KeyGesture ForChar(char c, KeyModifiers mods = KeyModifiers.None) =>
        new(KeyCode.Char, mods, new Rune(c));

    public bool Matches(KeyEvent key) =>
        key.Modifiers == Mods
        && (Code == KeyCode.Char || key.Key == KeyCode.Char
            ? Code == key.Key && Symbol == key.Character
            : Code == key.Key);
}

/// <summary>
/// A named set of key bindings owned by one widget. Entries are stored newest
/// first, so the first match wins and a rebind replaces the older entry rather
/// than shadowing it. Action ids stay opaque, so this is not a second action
/// vocabulary.
/// </summary>
public sealed class KeyContext
{
    private readonly List<ContextBinding> _entries = [];

    public KeyContext(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public int Count => _entries.Count;

    public IReadOnlyList<ContextBinding> Bindings => _entries;

    public void Bind(KeyGesture gesture, string actionId) => Insert(ContextBinding.Action(gesture, actionId));

    public void BindNoAction(KeyGesture gesture) => Insert(ContextBinding.NoAction(gesture));

    private void Insert(ContextBinding entry)
    {
        _entries.RemoveAll(e => e.Gesture.Equals(entry.Gesture));
        _entries.Insert(0, entry);
    }

    public bool Unbind(KeyGesture gesture) => _entries.RemoveAll(e => e.Gesture.Equals(gesture)) > 0;

    public void Clear() => _entries.Clear();

    public bool TryMatch(KeyEvent key, out ContextBinding hit)
    {
        foreach (ContextBinding entry in _entries)
        {
            if (entry.Gesture.Matches(key))
            {
                hit = entry;
                return true;
            }
        }

        hit = default;
        return false;
    }
}
