# Plan — Harbor.Tui.Abstractions

## Status: Deprecated facade — removal in v0.6

The R6 rename split the old TUI abstractions into `Harbor.Ui.Framework` (TEA state +
panels, shared by terminal and desktop shells) and `Harbor.Terminal.Abstractions`
(renderer/view/VM/plugin contracts). This project is an empty `ProjectReference`
shim kept only for build compatibility.

## Done

- [x] R6 split executed: code moved to `Harbor.Ui.Framework` + `Harbor.Terminal.Abstractions`
- [x] Facade project references both splits; zero types of its own (`Harbor.Tui.Abstractions.csproj:3-33`)
- [x] Package metadata marked deprecated
- [x] `InternalsVisibleTo` grants relocated to `Harbor.Ui.Framework.State.csproj`

## TODO

- [ ] Remove this facade project in v0.6 (update remaining ProjectReferences to the split projects)
- [ ] Sweep any docs that still describe ITuiRenderer/UiStore as "in Harbor.Tui.Abstractions"

## Known issues

- The facade must not gain types: adding them would mask migration debt. New contracts go to the split projects only.
