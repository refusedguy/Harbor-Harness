// #1618: the one using these files previously inherited from
// Harbor.Tui.CellForge/GlobalUsings.cs, narrowed to what the prompt engines
// actually bind:
//  - Harbor.Ui.Framework.Rendering.Input: KeyEvent / KeyCode are the
//    unqualified names ListPromptEngine's Update/HandleInput signatures speak,
//    and KeyModifierGate.AcceptsTypedChar is the extension the Char-key gate
//    calls. KeyModifiers is reached through the same namespace. The CellForge
//    root carries these as ALIASES (guards against engine-owned shadowing);
//    this assembly owns no shadowing vocabulary, so plain namespace imports
//    resolve the exact same Harbor.Ui.Framework.Rendering.Input types.
// The moved files keep their namespace verbatim (Harbor.Tui.CellForge.Widgets),
// so consumers keep their usings and only ProjectReferences change — and with
// zero line drift, file:line cites into the pair stand (KeyGateFamilyRules
// cites the ListPromptEngine path; CellForgeWidgetStateRules scans it).
global using Harbor.Ui.Framework.Rendering.Input;
