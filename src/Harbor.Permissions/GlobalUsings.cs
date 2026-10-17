// #1615: the usings these files previously inherited from
// Harbor.Application/GlobalUsings.cs, narrowed to what the Permissions surface binds:
//  - System.Text.Json: JsonElement argument reading (IPathExtractionPolicy.Extract).
//  - CSharpFunctionalExtensions: the Result railway in PermissionService
//    (ApprovalCoordinator and the policies are Result-free).
//  - Harbor.Abstractions.Agents: IAgentRegistry/AgentDefinition
//    (PermissionService ctor + registry lookup).
//  - Harbor.Abstractions.Permissions: the contract vocabulary — IPermissionService,
//    IApprovalCoordinator, PermissionRuleset/Rule, PermissionRequest/Response, the
//    ToolSafety* declarations and BuiltinToolSafetyProfiles (the Contracts assembly).
// Everything else (System + System.Linq via SDK ImplicitUsings, IO, Threading,
// Microsoft.Extensions.Logging) rides the SDK implicit usings or the files' own
// explicit using lists. Deliberately no ZLinq: the retired Harbor.Application
// global import bought nothing here — the surface's two LINQ calls
// (Enumerable.Sum, ToArray on a concrete List) bind plain System.Linq.
global using System.Text.Json;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Agents;
global using Harbor.Abstractions.Permissions;
