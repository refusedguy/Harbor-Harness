# SPRINT: Design System as Product

## Context
Harbor.DesignSystem + TerminalColorPalette is a moat: one design system powers ConsoleEx + Avalonia + Blazor. This sprint extracts it into a standalone, consumable library with documentation, theme marketplace, and public API.

## Tasks
1. Extract Harbor.DesignSystem as standalone NuGet
   - Package TerminalColorPalette, ChatPalette, DesignTokens
   - Zero dependencies on Harbor.Tui.ConsoleEx or Harbor.App.*
   - Acceptance: consumer app references only Harbor.DesignSystem; builds clean

2. Theme marketplace format
   - JSON schema for themes; validate + lint on load
   - ~/.harbor/themes/ directory with built-in + user themes
   - Acceptance: invalid theme JSON shows parse error, does not crash; valid theme loads live-reload

3. Public theme API docs
   - Generate docs from DesignTokens.cs XML comments
   - Theme guide: how to write a theme, override per-component, best practices
   - Acceptance: docs site builds; 3 example themes included (Dark, Light, Warm)

## Hard Rules
- Do NOT break existing internal Harbor apps; they must continue to compile without changes.
- Do NOT add Harbor.DesignSystem dependency on any UI framework.
- Keep the package MIT licensed; do not add proprietary formats.

## Deliverables
- 3 atomic commits with focused diffs
- Published package dry-run (nuget pack + local test consume)
- Test summary per commit
