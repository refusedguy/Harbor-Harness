// Renderer Unification (sprint: shared business logic):
// the renderer-agnostic vocabulary now lives in Harbor.Ui.Framework.Rendering.
// Global usings keep the CellForge sources churn-free — the old namespaces
// below no longer declare these types; the shared assembly is the single home.
global using Harbor.Ui.Framework.Rendering;
global using Harbor.Ui.Framework.Rendering.Input;
global using Harbor.Ui.Framework.Rendering.Markdown;
global using Harbor.Ui.Framework.Rendering.Widgets;
