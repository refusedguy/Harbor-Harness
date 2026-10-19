// #1618: the single using these files previously inherited from
// Harbor.Tui.CellForge/GlobalUsings.cs, narrowed to what the prompt engines
// actually bind — the key surface. This import brings in the KeyEvent and
// KeyCode names ListPromptEngine writes in its navigation entry points, and
// the AcceptsTypedChar extension on KeyModifiers that the Char-key gate
// calls. CellForge root reaches these names through key ALIASES, its guard
// against engine shadowing. This assembly owns no shadowing vocabulary, so a
// plain import resolves the same types from Harbor.Ui.Framework.Rendering.Input.
//
// The moved files keep their type home verbatim, Harbor.Tui.CellForge.Widgets,
// so consumers keep their usings and only ProjectReferences change. Zero line
// drift keeps every file:line cite into the pair standing: KeyGateFamilyRules
// cites the ListPromptEngine path and CellForgeWidgetStateRules scans it.
global using Harbor.Ui.Framework.Rendering.Input;
