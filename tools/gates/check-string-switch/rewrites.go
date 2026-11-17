package stringswitch

// rewrites.go -- the REVIEWED rewrites the `--apply` mode replays.
//
// This is the data of tools/string-switch-rewrites.py, ported as it
// stands: the same four rewrites, the same pairs, byte for byte. The
// python original kept the C# in a readable python module instead of
// inside a string literal, and the C# is still readable here -- it is
// the same text, in a Go raw string literal.
//
// A rewrite is a list of (old, new) text pairs for one file: the old
// text must occur EXACTLY once in the file, and the new text replaces
// it. A rewrite that no longer matches the tree is a hard error, not
// a silent no-op.

// rewrite is one reviewed rewrite.
type rewrite struct {
	path          string
	note          string
	expectRemoved int
	pairs         [][2]string
}

// rewrites is the reviewed rewrite set.
var rewrites = []rewrite{
	{
		path:          `src/Harbor.Tui.CellForge.Engine/Input/BindingsMap.cs`,
		note:          `KeySpec: the modifier and named-key vocabularies become two tables`,
		expectRemoved: 2,
		pairs: [][2]string{
			{`            return true;
        }

        switch (key.ToLowerInvariant())
        {
            case "space":
                gesture = KeyGesture.ForChar(' ', mods);
                return true;
            case "enter":
            case "return":
                gesture = KeyGesture.ForKey(KeyCode.Enter, mods);
                return true;
            case "tab":
                gesture = KeyGesture.ForKey(KeyCode.Tab, mods);
                return true;
            case "backspace":
            case "bspace":
                gesture = KeyGesture.ForKey(KeyCode.Backspace, mods);
                return true;
            case "escape":
            case "esc":
                gesture = KeyGesture.ForKey(KeyCode.Escape, mods);
                return true;
            case "up":
                gesture = KeyGesture.ForKey(KeyCode.Up, mods);
                return true;
            case "down":
                gesture = KeyGesture.ForKey(KeyCode.Down, mods);
                return true;
            case "left":
                gesture = KeyGesture.ForKey(KeyCode.Left, mods);
                return true;
            case "right":
                gesture = KeyGesture.ForKey(KeyCode.Right, mods);
                return true;
            case "home":
                gesture = KeyGesture.ForKey(KeyCode.Home, mods);
                return true;
            case "end":
                gesture = KeyGesture.ForKey(KeyCode.End, mods);
                return true;
            case "pageup":
            case "page_up":
            case "pgup":
                gesture = KeyGesture.ForKey(KeyCode.PageUp, mods);
                return true;
            case "pagedown":
            case "page_down":
            case "pgdn":
            case "pgdown":
                gesture = KeyGesture.ForKey(KeyCode.PageDown, mods);
                return true;
            case "insert":
            case "ins":
                gesture = KeyGesture.ForKey(KeyCode.Insert, mods);
                return true;
            case "delete":
            case "del":
                gesture = KeyGesture.ForKey(KeyCode.Delete, mods);
                return true;
            case "f1":
            case "f2":
            case "f3":
            case "f4":
            case "f5":
            case "f6":
            case "f7":
            case "f8":
            case "f9":
            case "f10":
            case "f11":
            case "f12":
                if (int.TryParse(key[1..], out int n) && n >= 1 && n <= 12)
                {
                    gesture = KeyGesture.ForKey((KeyCode)((int)KeyCode.F1 + (n - 1)), mods);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Canonical display for a gesture ("ctrl+j", "space", "enter").</summary>`, `            return true;
        }

        string named = key.ToLowerInvariant();
        if (named == "space")
        {
            gesture = KeyGesture.ForChar(' ', mods);
            return true;
        }

        if (NamedKeys.TryGetValue(named, out KeyCode code))
        {
            gesture = KeyGesture.ForKey(code, mods);
            return true;
        }

        return false;
    }

    /// <summary>Canonical display for a gesture ("ctrl+j", "space", "enter").</summary>`},
			{`        var mods = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= KeyModifiers.Ctrl;
                    break;
                case "shift":
                    mods |= KeyModifiers.Shift;
                    break;
                case "alt":
                case "option":
                    mods |= KeyModifiers.Alt;
                    break;
                case "meta":
                case "super":
                case "win":
                case "cmd":
                case "command":
                    mods |= KeyModifiers.Meta;
                    break;
                default:
                    return false;
            }
        }

        string key = parts[^1];`, `        var mods = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!ModifierBits.TryGetValue(parts[i].ToLowerInvariant(), out KeyModifiers bit))
            {
                return false;
            }

            mods |= bit;
        }

        string key = parts[^1];`},
			{`/// </summary>
public static class KeySpec
{
    /// <summary>Parses <paramref name="spec"/> into <paramref name="gesture"/>.</summary>
    /// <returns>False when the spec is empty, has no key, or names an unknown key.</returns>
    public static bool TryParse(string? spec, out KeyGesture gesture)`, `/// </summary>
public static class KeySpec
{
    /// <summary>
    /// The modifier names a spec may carry, and the bit each one sets.
    /// The lookup key is the lowercased token, which is exactly what the case
    /// ladder this table replaced compared against, so "CTRL" and "ctrl" still mean
    /// the same thing and "hyper" still means nothing.
    /// </summary>
    private static readonly FrozenDictionary<string, KeyModifiers> ModifierBits =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, KeyModifiers>(StringComparer.Ordinal)
            {
                ["ctrl"] = KeyModifiers.Ctrl,
                ["control"] = KeyModifiers.Ctrl,
                ["shift"] = KeyModifiers.Shift,
                ["alt"] = KeyModifiers.Alt,
                ["option"] = KeyModifiers.Alt,
                ["meta"] = KeyModifiers.Meta,
                ["super"] = KeyModifiers.Meta,
                ["win"] = KeyModifiers.Meta,
                ["cmd"] = KeyModifiers.Meta,
                ["command"] = KeyModifiers.Meta,
            },
            StringComparer.Ordinal);

    /// <summary>
    /// The named key specs, and the key code each one names. "space" is
    /// deliberately NOT in this table: it is the one spec that names a
    /// CHARACTER rather than a key code, and <see cref="TryParse" /> answers
    /// it before it looks here.
    /// </summary>
    private static readonly FrozenDictionary<string, KeyCode> NamedKeys =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, KeyCode>(StringComparer.Ordinal)
            {
                ["enter"] = KeyCode.Enter,
                ["return"] = KeyCode.Enter,
                ["tab"] = KeyCode.Tab,
                ["backspace"] = KeyCode.Backspace,
                ["bspace"] = KeyCode.Backspace,
                ["escape"] = KeyCode.Escape,
                ["esc"] = KeyCode.Escape,
                ["up"] = KeyCode.Up,
                ["down"] = KeyCode.Down,
                ["left"] = KeyCode.Left,
                ["right"] = KeyCode.Right,
                ["home"] = KeyCode.Home,
                ["end"] = KeyCode.End,
                ["pageup"] = KeyCode.PageUp,
                ["page_up"] = KeyCode.PageUp,
                ["pgup"] = KeyCode.PageUp,
                ["pagedown"] = KeyCode.PageDown,
                ["page_down"] = KeyCode.PageDown,
                ["pgdn"] = KeyCode.PageDown,
                ["pgdown"] = KeyCode.PageDown,
                ["insert"] = KeyCode.Insert,
                ["ins"] = KeyCode.Insert,
                ["delete"] = KeyCode.Delete,
                ["del"] = KeyCode.Delete,
                ["f1"] = KeyCode.F1,
                ["f2"] = KeyCode.F2,
                ["f3"] = KeyCode.F3,
                ["f4"] = KeyCode.F4,
                ["f5"] = KeyCode.F5,
                ["f6"] = KeyCode.F6,
                ["f7"] = KeyCode.F7,
                ["f8"] = KeyCode.F8,
                ["f9"] = KeyCode.F9,
                ["f10"] = KeyCode.F10,
                ["f11"] = KeyCode.F11,
                ["f12"] = KeyCode.F12,
            },
            StringComparer.Ordinal);

    /// <summary>Parses <paramref name="spec"/> into <paramref name="gesture"/>.</summary>
    /// <returns>False when the spec is empty, has no key, or names an unknown key.</returns>
    public static bool TryParse(string? spec, out KeyGesture gesture)`},
			{`// Same leaf rule as KeyBindings.cs: BCL-only, AOT-compatible, zero Harbor
// references, render-thread owned, chat untouched.

using System.Text;

namespace Harbor.Tui.CellForge.Input;`, `// Same leaf rule as KeyBindings.cs: BCL-only, AOT-compatible, zero Harbor
// references, render-thread owned, chat untouched.

using System.Collections.Frozen;
using System.Text;

namespace Harbor.Tui.CellForge.Input;`},
		},
	},
	{
		path:          `src/Harbor.Tui.CellForge.Engine/Capabilities/InlineImageProbe.cs`,
		note:          `the HARBOR_INLINE_IMAGE override vocabulary becomes one table`,
		expectRemoved: 1,
		pairs: [][2]string{
			{`{
    private const string OverrideVar = "HARBOR_INLINE_IMAGE";

    /// <summary>Detects the inline-image protocol for the current session.</summary>
    public static InlineImageKind Detect(Func<string, string?>? environmentLookup = null)
    {
        var lookup = environmentLookup ?? Environment.GetEnvironmentVariable;
        switch (NormalizeOverride(lookup(OverrideVar)))
        {
            case "off": return InlineImageKind.None;
            case "osc1337": return InlineImageKind.Osc1337;
            case "kitty": return InlineImageKind.KittyApc;
        }

        if (IsInsideMultiplexer(lookup))`, `{
    private const string OverrideVar = "HARBOR_INLINE_IMAGE";

    /// <summary>
    /// The <c>HARBOR_INLINE_IMAGE</c> values that name a protocol. The lookup
    /// key is the trimmed, lowercased override value, which is exactly what the case
    /// ladder this table replaced compared against. A value that is not in
    /// the table is not an override: the detection below still runs.
    /// </summary>
    private static readonly FrozenDictionary<string, InlineImageKind> OverrideKinds =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, InlineImageKind>(StringComparer.Ordinal)
            {
                ["off"] = InlineImageKind.None,
                ["osc1337"] = InlineImageKind.Osc1337,
                ["kitty"] = InlineImageKind.KittyApc,
            },
            StringComparer.Ordinal);

    /// <summary>Detects the inline-image protocol for the current session.</summary>
    public static InlineImageKind Detect(Func<string, string?>? environmentLookup = null)
    {
        var lookup = environmentLookup ?? Environment.GetEnvironmentVariable;
        string? overrideValue = NormalizeOverride(lookup(OverrideVar));
        if (overrideValue is not null && OverrideKinds.TryGetValue(overrideValue, out InlineImageKind overridden))
        {
            return overridden;
        }

        if (IsInsideMultiplexer(lookup))`},
			{`namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>Inline-image protocols the runtime can speak.</summary>`, `using System.Collections.Frozen;

namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>Inline-image protocols the runtime can speak.</summary>`},
		},
	},
	{
		path:          `src/Harbor.Ui.Framework.Projection/Projection/PanelRows.cs`,
		note:          `the todo marker vocabulary becomes one table, and the two parallel switches become one lookup`,
		expectRemoved: 2,
		pairs: [][2]string{
			{`            int contentBudget = Math.Max(1, width - 6);
            for (int i = 0; i < todos.Count; i++)
            {
                string icon = todos[i].Marker switch
                {
                    "[x]" or "[X]" => "✓",
                    "[~]" => "→",
                    "[ ]" => "○",
                    _ => "?",
                };
                switch (todos[i].Marker)
                {
                    case "[x]":
                    case "[X]":
                        done++;
                        break;
                    case "[~]":
                        active++;
                        break;
                    case "[ ]":
                        pending++;
                        break;
                }

                rows.Add($"  {icon}  {PanelText.Truncate(todos[i].Content, contentBudget)}");`, `            int contentBudget = Math.Max(1, width - 6);
            for (int i = 0; i < todos.Count; i++)
            {
                // A marker the table does not know renders the "?" fallback and
                // counts into no bucket, which is what the two switches gave.
                string icon = "?";
                if (TodoMarkers.TryGetValue(todos[i].Marker, out var marker))
                {
                    icon = marker.Icon;
                    switch (marker.Bucket)
                    {
                        case TodoBucket.Done:
                            done++;
                            break;
                        case TodoBucket.Active:
                            active++;
                            break;
                        case TodoBucket.Pending:
                            pending++;
                            break;
                    }
                }

                rows.Add($"  {icon}  {PanelText.Truncate(todos[i].Content, contentBudget)}");`},
			{`/// </summary>
public static class PanelRows
{
    /// <summary>Todo-list rows: header, Spectre-parity icons, done/active/pending summary.</summary>
    public static List<string> TodoRows(IReadOnlyList<TodoItem> todos, int width)
    {`, `/// </summary>
public static class PanelRows
{
    /// <summary>
    ///     The todo markers the panel understands: the icon each one renders
    ///     and the summary bucket it counts into. One table, so the icon and
    ///     the count cannot disagree about what a marker means. They used to
    ///     be two parallel switches over the same string, and a marker added
    ///     to one and not the other rendered an icon and counted nothing.
    /// </summary>
    /// <remarks>
    ///     The freeze is spelled out rather than left to the extension method
    ///     on purpose: this file already imports
    ///     <c>Harbor.Abstractions.Extensions</c> (for <c>Ensure</c> and
    ///     <c>Truncate</c>), and that namespace carries its own
    ///     <c>ToFrozenDictionary</c>, so the extension form is ambiguous here
    ///     (CS0121). The static call names the one that is meant.
    /// </remarks>
    private static readonly FrozenDictionary<string, (string Icon, TodoBucket Bucket)> TodoMarkers =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, (string Icon, TodoBucket Bucket)>(StringComparer.Ordinal)
            {
                ["[x]"] = ("✓", TodoBucket.Done),
                ["[X]"] = ("✓", TodoBucket.Done),
                ["[~]"] = ("→", TodoBucket.Active),
                ["[ ]"] = ("○", TodoBucket.Pending),
            },
            StringComparer.Ordinal);

    /// <summary>Which summary bucket a todo marker counts into.</summary>
    private enum TodoBucket : byte
    {
        /// <summary>Finished.</summary>
        Done = 0,

        /// <summary>In progress.</summary>
        Active = 1,

        /// <summary>Not started.</summary>
        Pending = 2,
    }

    /// <summary>Todo-list rows: header, Spectre-parity icons, done/active/pending summary.</summary>
    public static List<string> TodoRows(IReadOnlyList<TodoItem> todos, int width)
    {`},
			{`using System.Globalization;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models.Identifiers;`, `using System.Collections.Frozen;
using System.Globalization;
using Harbor.Abstractions.Extensions;
using Harbor.Abstractions.Models.Identifiers;`},
		},
	},
	{
		path:          `src/Harbor.Hosting/Modules/TelemetryModule.cs`,
		note:          `the HARBOR_TELEMETRY_EXPORT vocabulary becomes one table`,
		expectRemoved: 1,
		pairs: [][2]string{
			{`        string? rawTarget = read(HarborEnv.TelemetryExport);
        if (!string.IsNullOrWhiteSpace(rawTarget))
        {
            switch (rawTarget.Trim().ToLowerInvariant())
            {
                case "none":
                case "off":
                    target = TelemetryExportTarget.None;
                    break;
                case "console":
                    target = TelemetryExportTarget.Console;
                    break;
                case "file":
                    target = TelemetryExportTarget.File;
                    break;
                default:
                    TelemetryModuleLog.UnknownExportTarget(logger, HarborEnv.TelemetryExport, rawTarget);
                    return null;
            }
        }

        if (target == TelemetryExportTarget.None)`, `        string? rawTarget = read(HarborEnv.TelemetryExport);
        if (!string.IsNullOrWhiteSpace(rawTarget))
        {
            if (!ExportTargets.TryGetValue(rawTarget.Trim().ToLowerInvariant(), out TelemetryExportTarget resolved))
            {
                TelemetryModuleLog.UnknownExportTarget(logger, HarborEnv.TelemetryExport, rawTarget);
                return null;
            }

            target = resolved;
        }

        if (target == TelemetryExportTarget.None)`},
			{`/// </summary>
internal static class TelemetryModule
{
    /// <summary>
    ///     Register the tracer/metrics singletons plus the event-bus queue-age
    ///     reporter (#47/S2).`, `/// </summary>
internal static class TelemetryModule
{
    /// <summary>
    ///     The <c>HARBOR_TELEMETRY_EXPORT</c> values that name a sink. The
    ///     lookup key is the trimmed, lowercased environment value, which is
    ///     what the case ladder this table replaced compared against. A value
    ///     that is not in the table is an unknown target: it is reported and
    ///     the sink stays off, never guessed.
    /// </summary>
    private static readonly FrozenDictionary<string, TelemetryExportTarget> ExportTargets =
        FrozenDictionary.ToFrozenDictionary(
            new Dictionary<string, TelemetryExportTarget>(StringComparer.Ordinal)
            {
                ["none"] = TelemetryExportTarget.None,
                ["off"] = TelemetryExportTarget.None,
                ["console"] = TelemetryExportTarget.Console,
                ["file"] = TelemetryExportTarget.File,
            },
            StringComparer.Ordinal);

    /// <summary>
    ///     Register the tracer/metrics singletons plus the event-bus queue-age
    ///     reporter (#47/S2).`},
			{`using System.Globalization;
using Harbor.Abstractions.Env;
using Harbor.Abstractions.Events;`, `using System.Collections.Frozen;
using System.Globalization;
using Harbor.Abstractions.Env;
using Harbor.Abstractions.Events;`},
		},
	},
}
