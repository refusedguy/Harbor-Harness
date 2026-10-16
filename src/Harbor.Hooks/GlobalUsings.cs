// #1613: the usings these files previously inherited from
// Harbor.Application/GlobalUsings.cs, narrowed to what the Hooks surface binds:
//  - System.Text.Json(+Serialization): HookConfig load, HookPayload serialize,
//    HookVerdictDto parse, HookJsonContext source-gen attributes.
//  - Harbor.Abstractions.Models: ToolResult (IHookRunner.RunPostToolUseAsync).
// Everything else (System, IO, Threading, Logging) rides SDK implicit usings
// or the files' own explicit using lists. Deliberately no ZLinq /
// CSharpFunctionalExtensions: this surface uses neither.
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using Harbor.Abstractions.Models;
