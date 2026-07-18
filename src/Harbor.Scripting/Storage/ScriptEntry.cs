// Storage layer — pure script file storage abstraction.
//
// Layering rule (see docs/SCRIPTING.md §Architecture):
//   This layer knows NOTHING about engines, compilation, or the Harbor bridge.
//   It only knows: "list / read / write / delete script files in some store."
//   Implementations may target filesystems, in-memory dicts, ZIP archives, etc.
namespace Harbor.Scripting.Storage;

/// <summary>
///     A discovered script entry: its name, location, content, and metadata.
/// </summary>
/// <param name="Name">Stable script name (file stem, e.g. <c>greet</c> for <c>greet.ts</c>).</param>
/// <param name="Path">Opaque location identifier (filesystem path, URI, in-memory key).</param>
/// <param name="Content">Script source code.</param>
/// <param name="Hash">SHA-256 hex hash of <paramref name="Content" />, for change detection.</param>
/// <param name="LastModified">Last modification time (UTC), or <c>null</c> if unknown.</param>
public sealed record ScriptEntry(
    string Name,
    string Path,
    string Content,
    string Hash,
    DateTimeOffset? LastModified);
