// Renderer Unification (sprint: shared business logic):
// the renderer-agnostic vocabulary now lives in Harbor.Ui.Framework.Rendering.
// Global usings keep the CellForge sources churn-free — the old namespaces
// below no longer declare these types; the shared assembly is the single home.
global using Harbor.Ui.Framework.Rendering;
global using Harbor.Ui.Framework.Rendering.Input;
global using Harbor.Ui.Framework.Rendering.Markdown;
global using Harbor.Ui.Framework.Rendering.Widgets;

// #436: the engine owns same-named primitives now (Harbor.Tui.CellForge.*,
// zero Harbor references). These aliases pin the HISTORICAL meaning — the
// Rendering vocabulary — for every file in this assembly, so the chat-owned
// files that moved here from the engine (Chat/Rendering/) keep compiling
// unchanged. A using-alias beats every namespace import, global or not, so
// this is deterministic per file. Sites that genuinely speak the ENGINE grid
// (ScreenSession, DiffEngine/Flush call sites) qualify explicitly through a
// file-level `using EngineCells = Harbor.Tui.CellForge.Rendering;`.
global using Cell = Harbor.Ui.Framework.Rendering.Cell;
global using Rect = Harbor.Ui.Framework.Rendering.Rect;
global using ScreenBuffer = Harbor.Ui.Framework.Rendering.ScreenBuffer;
global using CellStyle = Harbor.Ui.Framework.Rendering.CellStyle;
global using PackedColor = Harbor.Ui.Framework.Rendering.PackedColor;
global using StyleAttr = Harbor.Ui.Framework.Rendering.StyleAttr;
global using UnicodeWidth = Harbor.Ui.Framework.Rendering.UnicodeWidth;
global using KeyEvent = Harbor.Ui.Framework.Rendering.Input.KeyEvent;
global using KeyCode = Harbor.Ui.Framework.Rendering.Input.KeyCode;
global using KeyModifiers = Harbor.Ui.Framework.Rendering.Input.KeyModifiers;
global using KeyEventType = Harbor.Ui.Framework.Rendering.Input.KeyEventType;
global using IFocusTarget = Harbor.Ui.Framework.Rendering.Input.IFocusTarget;
global using UiKeyDto = Harbor.Ui.Framework.Rendering.Input.UiKeyDto;
global using UiKeyKind = Harbor.Ui.Framework.Rendering.Input.UiKeyKind;
global using UiKeyMods = Harbor.Ui.Framework.Rendering.Input.UiKeyMods;
