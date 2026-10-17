// #1617: the usings these files previously inherited from
// Harbor.Tui.CellForge/GlobalUsings.cs, narrowed to what the syntax/theme surface binds:
//  - Harbor.Ui.Framework.Rendering: CellStyle (spans paint into Rendering grids —
//    the #436 pin every tokenizer file carries as its UIR alias).
//  - Harbor.Ui.Framework.Rendering.Markdown: MdLine/MdStyle (HighlightFenceBodies
//    walks an already-rendered MdLine list).
//  - Harbor.Ui.Framework.Rendering.Widgets: ChatPalette (the live palette the
//    default CodeHighlightPalette projects from).
// The moved files keep their namespaces verbatim (Harbor.Tui.CellForge.Rendering
// and Harbor.Tui.CellForge.Widgets), so consumers keep their usings and only
// ProjectReferences change. Lines above duplicate the files' own using
// directives on purpose: these are global, the files' are file-scoped, and both
// resolve the same vocabulary.
global using Harbor.Ui.Framework.Rendering;
global using Harbor.Ui.Framework.Rendering.Markdown;
global using Harbor.Ui.Framework.Rendering.Widgets;
