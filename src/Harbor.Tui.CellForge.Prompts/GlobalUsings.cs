// #1618: the one using these files previously inherited from
// Harbor.Tui.CellForge/GlobalUsings.cs, narrowed to what the prompt engines
// actually bind:
//  - Harbor.Ui.Framework.Rendering.Input is where the KeyEvent and KeyCode
//    names ListPromptEngine speaks in its Update/HandleInput signatures
//    live, plus the KeyModifierGate.AcceptsTypedChar extension the Char-key
//    gate calls (KeyModifiers travels through the same home). CellForge root
//    reaches these names through key ALIASES, its guard against engine
//    shadowing; this assembly owns no shadowing vocabulary, so the plain
//    import resolves the exact same Harbor.Ui.Framework.Rendering.Input
//    types.
// The moved files keep their type home verbatim
// (Harbor.Tui.CellForge.Widgets), so consumers keep their usings and only
// ProjectReferences change — and with zero line drift, file:line cites into
// the pair stand (KeyGateFamilyRules cites the ListPromptEngine path;
// CellForgeWidgetStateRules scans it).
global using Harbor.Ui.Framework.Rendering.Input;
