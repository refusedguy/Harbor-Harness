# docs/notes

Working notes rescued from the repo-root `temp/` scratch directory during the
sprint-2 cleanup (zone G4). These are historical debugging notes — not
maintained documentation.

| File | Origin |
|---|---|
| `avalonia-boxshadow-diagnosis.md` | Diagnosis of the Avalonia 12 `BoxShadow` vs `BoxShadows` resource-setter crash (XamlIL dynamic setters + string-typed resources). Fix guidance: use `<BoxShadows>` tokens and enable full `AvaloniaUseCompiledXaml`. |
| `kilocode-default-model-probe.md` | Dated measurement for #937: what `api.kilo.ai/api/gateway/models` serves without a key. `tencent/hy3:free` is absent, `kilo-auto/free` is present and free. Records the fork's two provenances and why the replacement value is the owner's call, not this file's. |
