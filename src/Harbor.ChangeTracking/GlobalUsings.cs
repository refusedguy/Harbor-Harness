// #1616: global usings for the extracted verified-change pipeline. Only the
// namespaces the moved files actually bind beyond the implicit ones —
// System.Text.Json (manifest/checks/report parsing) and CSharpFunctionalExtensions
// (every public surface here speaks Result). The Abstractions namespaces carry
// the shared run vocabulary the pipeline reads rather than owns: RunId
// (Models.Identifiers) and WorkspaceContract/WorkspaceIsolation (Models).
global using System.Text.Json;
global using CSharpFunctionalExtensions;
global using Harbor.Abstractions.Models;
global using Harbor.Abstractions.Models.Identifiers;
