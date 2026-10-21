// The usings the moved files inherited from Harbor.Application/GlobalUsings.cs,
// narrowed to exactly what the configuration cohort resolves WITHOUT a
// file-local using.
//
// What was dropped, and why (measured, not guessed — the cohort's 11 files were
// scanned for every public type of each dropped namespace):
//
//  - Harbor.Abstractions.Events   — no agent/turn/message event type is
//    referenced by the configuration family.
//  - Harbor.Abstractions.Models   — no AgentMessage / ToolResult / Session
//    type is referenced by the configuration family.
//  - Harbor.Abstractions.Providers — the provider vocabulary the configuration
//    side binds is ProviderId / ModelRef, and those live in
//    Harbor.Abstractions.Models.Identifiers, not here.
//  - Harbor.Abstractions.Sessions — no session/compaction type is referenced.
//  - Harbor.Abstractions.Tools    — the only tool type the configuration
//    family references is the tool-output spill policy (#1748), and
//    ConfigSections.cs imports it FILE-LOCALLY: the global set stays narrowed
//    to what the cohort resolves without a file-local using.
//
// What stays, and who needs it:
//
//  - ZLinq: the zero-allocation LINQ drop-in. AuthStore's
//    `envVars.Select(...).JoinToString(", ")` needs it — JoinToString is a
//    ZLinq operator, not a System.Linq one — and ProviderPresets projects
//    with FirstOrDefault / Where / ToList.
//  - System.Text.Json: JsonDocument / JsonElement in ProviderPresetCatalog and
//    ConfigStore (the SDK's implicit usings do not carry it).
//  - System.Text.Json.Serialization: ConfigSections declares JSON attributes
//    with no file-local using.
//  - CSharpFunctionalExtensions: the Result / Maybe railway in ConfigStore,
//    ConfigNormalizer, HarborConfig, AuthStore and ProviderPresetCatalog.
//  - Harbor.Abstractions.Agents: AgentDefinition (AgentStepBudget).
//  - Harbor.Abstractions.Models.Identifiers: ProviderId / ModelRef /
//    AgentName — the strongly-typed identity the configuration normalizer and
//    the identity section are built on.
//  - Harbor.Abstractions.Permissions: PermissionRule / PermissionAction —
//    the permissions section of the persisted config.
//
// Everything else (System, System.Collections.Generic, System.IO, System.Threading,
// System.Threading.Tasks) rides the SDK's implicit usings; ResultErrors comes
// from the file-local `using Harbor.Abstractions.Results;` in ConfigStore.
global using ZLinq;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Agents;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Abstractions.Permissions;
