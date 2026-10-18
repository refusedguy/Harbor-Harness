// #1617: the usings these files previously inherited from
// Harbor.Tui.CellForge/GlobalUsings.cs, narrowed to what the syntax/theme surface binds:
//  - Harbor.Ui.Framework.Rendering: CellStyle (spans paint into Rendering grids —
//    the #436 pin every tokenizer file carries as its UIR alias).
//  - Harbor.Ui.Framework.Rendering.Markdown: MdLine/MdStyle (HighlightFenceBodies
//    walks an already-rendered MdLine list).
//  - Harbor.Ui.Framework.Rendering.Widgets: ChatPalette (the live palette the
//    default CodeHighlightPalette projects from).
//  - Harbor.Ui.Framework.Projection: RgbColor — the 14-slot theme vocabulary
//    HarborTheme carries. Harbor.DesignSystem maps this namespace in ITSELF via
//    ITS GlobalUsings.cs, which made the old projection using look dead when the
//    files left CellForge; once the files moved out, they resolved RgbColor no
//    more (CI build CS0246 at JsonThemeLoader.cs:53-62,101). Re-added here as a
//    global so JsonThemeLoader's unqualified RgbColor and doc references keep
//    compiling with zero line drift in the moved file.
// The moved files keep their namespaces verbatim (Harbor.Tui.CellForge.Rendering
// and Harbor.Tui.CellForge.Widgets), so consumers keep their usings and only
// ProjectReferences change. Lines above duplicate the files' own using
// directives on purpose: these are global, the files' are file-scoped, and both
// resolve the same vocabulary.
global using Harbor.Ui.Framework.Rendering;
global using Harbor.Ui.Framework.Rendering.Markdown;
global using Harbor.Ui.Framework.Rendering.Widgets;
global using Harbor.Ui.Framework.Projection;
