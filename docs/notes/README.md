# docs/notes

Working notes rescued from the repo-root `temp/` scratch directory during the
sprint-2 cleanup (zone G4). These are historical debugging notes — not
maintained documentation.

| File | Origin |
|---|---|
| `avalonia-boxshadow-diagnosis.md` | Diagnosis of the Avalonia 12 `BoxShadow` vs `BoxShadows` resource-setter crash (XamlIL dynamic setters + string-typed resources). Fix guidance: use `<BoxShadows>` tokens and enable full `AvaloniaUseCompiledXaml`. |
