using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>Shared helpers for golden-byte tests: raw byte strings → parser → events.</summary>
internal static class T
{
    public const string Esc = "\u001B";
    public const string Bel = "\u0007";

    /// <summary>Feeds each chunk as a separate read (chunk boundaries are part of the vector).</summary>
    public static InputEvent[] Feed(EscapeSequenceParser parser, params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            parser.Parse(Encoding.UTF8.GetBytes(chunk));
        }

        return Drain(parser);
    }

    public static InputEvent[] FeedBytes(EscapeSequenceParser parser, params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            parser.Parse(chunk);
        }

        return Drain(parser);
    }

    public static InputEvent[] Drain(EscapeSequenceParser parser)
    {
        var events = new List<InputEvent>(parser.AvailableEvents);
        parser.DrainEvents(events);
        return events.ToArray();
    }

    public static InputEvent Single(InputEvent[] events, int index = 0) => events[index];
}

/// <summary>Assertion helpers for typed input events.</summary>
internal static class A
{
    public static async Task IsKey(
        InputEvent evt,
        KeyCode key,
        KeyModifiers mods = KeyModifiers.None,
        KeyEventType type = KeyEventType.Press,
        bool kitty = false)
    {
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Key);
        await Assert.That(evt.Key.Key).IsEqualTo(key);
        await Assert.That(evt.Key.Modifiers).IsEqualTo(mods);
        await Assert.That(evt.Key.EventType).IsEqualTo(type);
        await Assert.That(evt.Key.IsKittyEncoded).IsEqualTo(kitty);
    }

    public static async Task IsChar(InputEvent evt, Rune character, KeyModifiers mods = KeyModifiers.None, bool kitty = false)
    {
        await Assert.That(evt.Kind).IsEqualTo(InputEventKind.Key);
        await Assert.That(evt.Key.Key).IsEqualTo(KeyCode.Char);
        await Assert.That(evt.Key.Character).IsEqualTo(character);
        await Assert.That(evt.Key.Modifiers).IsEqualTo(mods);
        await Assert.That(evt.Key.IsKittyEncoded).IsEqualTo(kitty);
    }
}
