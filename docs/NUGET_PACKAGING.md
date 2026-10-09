# NuGet packaging for the Harbor.* library set

Pack + validation for every packable `Harbor.*` library as part of issue #429.
Publishing to nuget.org is deliberately **not** built yet: the pack must stay
green on every PR long before any release feed is configured.

## What CI does

The `build` job in `.github/workflows/ci.yml` packs and validates — steps
inside the existing job, no new job or matrix axis:

1. `dotnet pack Harbor.slnx -c Release --no-build -p:ContinuousIntegrationBuild=true -o out/packages`
   Pack warnings fail the job (`TreatWarningsAsErrors` in `Directory.Build.props`).
2. `python3 tools/pack-validate.py out/packages` — for each of the 43 packable
   `src/` projects asserts the `.nupkg` exists and carries: non-empty description,
   MIT license expression, `RepositoryUrl`/`RepositoryType` pointing at
   `refusedguy/Harbor-Harness`, a project URL, at least 3 tags, `README.md` at
   the package root, and `lib/net10.0/*.dll`.
3. Double-pack byte-identical check — packing twice from the same commit must
   yield identical `.nupkg` files.
4. Clean-room smoke install — a throwaway console project installs
   `Harbor.Abstractions` + `Harbor.Providers.OpenAiCompatible` from the local
   feed (transitives from nuget.org) and builds, proving the graph resolves.
5. The `.nupkg` set is uploaded as the `nuget-packages` artifact (7 days).

## The packable set

43 of the 52 `src/` projects are packable libraries. Per-project `csproj`
files declare the reviewable metadata (`IsPackable`, `PackageId`,
`Description`, `PackageTags`); shared mechanics (README embed) live in
`src/Directory.Build.targets`.

Non-packable projects carry an explicit `IsPackable=false` with a one-line
reason:

| Project | Why not packable |
|---|---|
| `Harbor.CodeGen` | Build-time Roslyn component, ships as an analyzer, not a runtime library |
| `Harbor.Logging` | Internal Serilog bootstrap composed by app hosts, not a public contract |
| `Harbor.Plugins.Host` | `OutputType=Exe` out-of-process plugin host, deployed as a binary |
| `Harbor.Ui.Framework.Abstractions` | Intermediate module, ships via the `Harbor.Ui.Framework` meta-package |
| `Harbor.Ui.Framework.Projection` | Intermediate module, ships via the `Harbor.Ui.Framework` meta-package |
| `Harbor.Ui.Framework.Services` | Intermediate module, ships via the `Harbor.Ui.Framework` meta-package |
| `Harbor.Ui.Framework.Sessions` | Intermediate module, ships via the `Harbor.Ui.Framework` meta-package |
| `Harbor.Ui.Framework.State` | Intermediate module, ships via the `Harbor.Ui.Framework` meta-package |
| `Harbor.Ui.Framework.ViewModels` | Intermediate module, ships via the `Harbor.Ui.Framework` meta-package |

(`Harbor.Providers.Shared` and `Harbor.Storage.Shared` have no `.csproj` at
all — linked source compiled into their sibling packages. `Harbor.App.Cli`
packs separately as a `PackAsTool` tool package; the binary release job is
unchanged.)

## Local dry run

```bash
dotnet pack Harbor.slnx -c Release -p:ContinuousIntegrationBuild=true -o out/packages
python3 tools/pack-validate.py out/packages
python3 tools/pack-validate.py --selftest
```

## Deferred (not this slice)

- Pack-and-push job in `release.yml` gated on a `v*` tag (needs a feed + API secret).
- Symbol packages (`.snupkg`): Release sets `DebugType=none`, so there are no
  pdbs to ship yet.
- SourceLink / `RepositoryCommit` embedding.
- Version/`AssemblyVersion` policy for the frozen contract assemblies
  (still `<Version>0.4.0-alpha</Version>` in `Directory.Build.props`).
