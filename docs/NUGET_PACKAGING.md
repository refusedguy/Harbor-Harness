# NuGet packaging (issue #429)

Every `Harbor.*` library ships to NuGet as part of v1.0. This page records
the packable set and the CI slice that validates it. Publishing is a
follow-up (see "Out of scope").

## Packable set

44 of the 52 `src/` projects are packable (`IsPackable=true`, explicit in
every `.csproj`). Shared metadata (MIT license, repository URL, project URL)
lives in `Directory.Build.props`; per-project `PackageId`, `Description`,
`PackageTags`, and `PackageReadmeFile` (embedding the per-project
`README.md`) live in each `.csproj`.

Not packable (`IsPackable=false` or no `.csproj` at all):

| Project | Why excluded |
|---|---|
| `Harbor.CodeGen` | Build-time Roslyn component (`IsRoslynComponentPackage`), not a runtime library |
| `Harbor.Logging` | Internal Serilog bootstrap, not a public API surface |
| `Harbor.Plugins.Host` | `OutputType=Exe` — out-of-process MCP stdio host |
| `Harbor.Ui.Framework.Abstractions` | Implementation submodule, consumed via the `Harbor.Ui.Framework` meta-package |
| `Harbor.Ui.Framework.State` | Same as above |
| `Harbor.Ui.Framework.ViewModels` | Same as above |
| `Harbor.Ui.Framework.Services` | Same as above |
| `Harbor.Ui.Framework.Projection` | Same as above |
| `Harbor.Ui.Framework.Sessions` | Same as above |
| `Harbor.Providers.Shared` | Linked source, no `.csproj` (compiled into each provider) |
| `Harbor.Storage.Shared` | Linked source, no `.csproj` (compiled into Jsonl + Sqlite stores) |

`Harbor.Transport.Remote` used to rely on the SDK default (no `IsPackable`
at all); it now states `IsPackable=true` with full metadata like the rest.

## CI validation

The `build` job in `.github/workflows/ci.yml` packs every packable project
(`dotnet pack --no-build`, `ContinuousIntegrationBuild=true`, so the payload
is the same Release build the job just verified) and then checks:

1. Every packable project produced exactly one `.nupkg` (no more, no less).
2. Each `.nuspec` carries the MIT license expression, the real repository
   URL, a project URL, a non-empty description, and at least 3 tags.
3. Each `.nupkg` embeds its per-project `README.md`.
4. Packing twice from the same checkout yields byte-identical `.nupkg` files.
5. A throwaway console project clean-room installs `Harbor.Abstractions`
   plus one provider package from the just-packed local feed, proving the
   dependency graph resolves.

The produced packages are uploaded as the `harbor-nuget-packages` artifact
(7-day retention). `dotnet nuget verify` is deliberately not used: it
validates signatures, and unsigned CI builds would fail it.

## Out of scope (follow-up slices)

- **Publish**: a `release.yml` pack-and-push job gated on a `v1.*` tag,
  pushing to nuget.org and/or GitHub Packages. Needs an API secret
  (`NUGET_API_KEY`) — not wired in this slice.
- **Symbol packages** (`.snupkg` via `IncludeSymbols`).
- **Versioning**: `Directory.Build.props` still pins
  `<Version>0.4.0-alpha</Version>`; align it with the release tag and freeze
  `AssemblyVersion` for the contract assemblies per the API-freeze slice.
- **Meta-package dependencies**: `Harbor.Ui.Framework` references
  never-published submodule IDs; decide `PrivateAssets` vs. publishing the
  submodules before the first push.
