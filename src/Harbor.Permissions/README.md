# Harbor.Permissions

Harbor's permissions subdomain — the runtime half of the tool-permission
pipeline, extracted verbatim from `Harbor.Application/Permissions` (#1615).
Holds the Specification-pattern permission evaluator, the runtime-owned
approval gate and the pluggable rule-argument extraction policies.

## What's in here

| File | Responsibility |
|------|----------------|
| `PermissionService.cs` | Specification-pattern `IPermissionService`: looks up the agent's `PermissionRuleset`, applies allow/deny/ask, merges persisted "always" decisions, downgrades out-of-workspace Allows to Ask (A1/A2). |
| `ApprovalCoordinator.cs` | Runtime-owned approval/cancellation gate (#49): the single ingress for run cancellation and the linearization point for gate decisions vs cancel. |
| `IPermissionConfigStore.cs` | The subdomain's one outward seam: the persisted-permissions section of `config.json`, cut off the whole-config `IConfigStore`. |
| `IPathExtractionPolicy.cs` | Strategy interface for rule-argument extraction plus `DefaultPathExtractionPolicies` (#178). |
| `PathArgExtractionPolicy.cs` | Normalized `path`-argument extraction for path-taking tools (tool set derived from `BuiltinToolSafetyProfiles`, #595). |
| `LegacyArgExtractionPolicy.cs` | Terminal raw-argument fallback (total dispatch). |
| `PermissionPathNormalizer.cs` | Workspace-root path normalization shared by the policies. |

## Files & namespaces

Namespace stays `Harbor.Application.Permissions` (verbatim move, the same
convention as the Harbor.Hooks extraction #1613): consumers keep their
usings, only ProjectReferences change. The move is reflection of residence,
not of a rename — `IPermissionService`/`IApprovalCoordinator` contracts were
already Domain (`Harbor.Abstractions.Contracts` / `Harbor.Abstractions`) and
did not move.

## Wiring

Composition roots wire `PermissionService`/`ApprovalCoordinator` directly
(`IntelligenceModule`, `CellForgeModule` — both reference this assembly). The
persisted-decision store arrives pre-adapted: `ConfigurationModule` registers
`IPermissionConfigStore` over `IConfigStore` via `PermissionConfigStoreAdapter`
(Harbor.Application), so this assembly never binds `HarborConfig` or the
whole-config store. `Harbor.Application` itself binds only the contracts
(`IPermissionService`, `IApprovalCoordinator`) and keeps a ProjectReference to
this assembly for the seam implementation.

## Usage

`PermissionService.CheckAsync(agent, tool, args)` rides one
`Result<PermissionResponse>` railway inside the tool-dispatch hot path; a
permissions-persisted decision ("always") short-circuits the Ask prompt.
`SaveAsync` persists the merged decisions atomically through the seam.

## Dependencies

`Harbor.Abstractions` (+ `Harbor.Abstractions.Contracts` transitively, for
the `PermissionRuleset`/`ToolSafetyDeclaration` vocabulary),
`CSharpFunctionalExtensions` (the `Result` railway in `PermissionService`),
`Microsoft.Extensions.Logging.Abstractions` (the `[LoggerMessage]` factory
class). No Infrastructure, no Presentation, no Harbor.Application.

## Limitations

The seam interface is section-shaped on purpose: the whole-config
`IConfigStore` surface (API keys, model fields) is *not* part of this
subdomain's vocabulary — nobody here should grow a dependency on
Configuration's store types again. Persistence semantics (validate-on-write)
belong to the underlying `IConfigStore` implementation and are unchanged by
the extraction. Known-gate ratchets: `ApprovalGateWaiterRaceRules` and the
`tests/Harbor.Application.Tests/ApprovalCoordinatorTests.cs` pin the lock
discipline in this file — see the remarks inside `ApprovalCoordinator.cs`.
