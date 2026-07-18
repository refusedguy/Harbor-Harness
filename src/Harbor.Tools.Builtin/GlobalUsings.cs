// Harbor.Tools.Builtin — empty GlobalUsings.
//
// This project is now a THIN FACADE: it contains no .cs code of its own, only
// a .csproj that references the 14 individual leaf tool projects split out of
// the original god-project. The namespace `Harbor.Tools.Builtin` is preserved
// by every leaf tool's .cs file (each declares `namespace Harbor.Tools.Builtin;`)
// so existing consumers using `using Harbor.Tools.Builtin;` keep compiling
// without code changes.
