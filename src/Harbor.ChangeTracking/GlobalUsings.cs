// #1616: global usings for the extracted verified-change pipeline. Only the
// namespaces the moved files actually bind beyond the implicit ones —
// System.Text.Json (manifest/checks/report parsing) and CSharpFunctionalExtensions
// (every public surface here speaks Result). The Abstractions namespaces carry
// the shared run vocabulary the pipeline reads rather than owns: RunId
// (Models.Identifiers) and WorkspaceContract/WorkspaceIsolation (Models). The
// staying Application side is read through the ProjectReference the same way:
// Harbor.Application.Sessions declares WorkspacePaths / IWorkspacePaths
// (sessions/WorkspacePaths.cs) — the pipeline consumes them, nothing
// re-exports. The run records moved with the session-context cohort (#1624):
// IsolatedWorkspace / RunManifest / RunState now live in
// Harbor.SessionContext, in the SAME Harbor.Application.Sessions namespace,
// so this using still resolves them — the assembly edge is what changed.
global using System.Text.Json;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
global using Harbor.Application.Sessions;
