// Shared rendering vocabulary moved to Harbor.Ui.Framework.Rendering
// (renderer-unification sprint). Global usings keep the test files churn-free.
global using Harbor.Ui.Framework.Rendering;
global using Harbor.Ui.Framework.Rendering.Input;
global using Harbor.Ui.Framework.Rendering.Markdown;
global using Harbor.Ui.Framework.Rendering.Widgets;
global using Harbor.TestKit;

// #436: same shared-name pins as Harbor.Tui.CellForge/GlobalUsings.cs — the
// Rendering vocabulary keeps its historical meaning; engine-typed sites
// qualify through EngineCells (grid) / EngineInput (keys).
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
global using EngineCells = Harbor.Tui.CellForge.Rendering;
global using EngineInput = Harbor.Tui.CellForge.Input;
