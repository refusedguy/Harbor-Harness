// #1616: global usings for the extracted verified-change pipeline. Only the
// namespaces the moved files actually bind beyond the implicit ones —
// System.Text.Json (manifest/checks/report parsing) and CSharpFunctionalExtensions
// (every public surface here speaks Result). The Abstractions namespaces carry
// the shared run vocabulary the pipeline reads rather than owns: RunId
// (Models.Identifiers) and WorkspaceContract/WorkspaceIsolation (Models). The
// staying Application side is read through the ProjectReference the same way:
// Harbor.Application.Sessions declares IsolatedWorkspace / RunManifest / RunState
// (sessions/IsolatedWorkspace.cs) and WorkspacePaths / IWorkspacePaths
// (sessions/WorkspacePaths.cs) — the pipeline consumes them, nothing re-exports.
global using System.Text.Json;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Application.Sessions;
