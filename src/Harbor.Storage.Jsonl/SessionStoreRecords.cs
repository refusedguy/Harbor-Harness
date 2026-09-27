// SessionStoreRecords.cs — JSONL session-file record shapes.
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). The store orchestrates I/O + caching; this file owns the
// on-disk schema (line-1 header, message lines, parsed-message cache entry).

using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     Parsed-message cache entry (§3.3). Records the file's last-write-time
///     at the moment of the parse so subsequent reads can detect freshness via
///     a single <c>File.GetLastWriteTimeUtc</c> call.
/// </summary>
internal sealed record SessionCacheEntry(
    DateTimeOffset FileLastWriteUtc,
    IReadOnlyList<AgentMessage> Messages);

/// <summary>
///     Line-1 header of a session file. Optional trailing fields carry the
///     newer <see cref="Harbor.Abstractions.Models.Session"/> attributes — before they
///     existed, UpdateAsync rewrote the header WITHOUT parent linkage/status/git/kind
///     fields and silently dropped them on first rename/rebind (V4-bugfix).
///     Defaults keep legacy files parseable.
/// </summary>
internal sealed record SessionHeaderEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt = default,
    [property: JsonPropertyName("parentSessionId")] string? ParentSessionId = null,
    [property: JsonPropertyName("status")] SessionStatus Status = SessionStatus.Idle,
    [property: JsonPropertyName("gitBranch")] string? GitBranch = null,
    [property: JsonPropertyName("gitIsDirty")] bool GitIsDirty = false,
    [property: JsonPropertyName("kind")] SessionKind Kind = SessionKind.User);

/*
 * DDD-audit 25.08 (ROP-C Z3): <see cref="JsonlSessionStore.GetAsync" /> used to
 * fabricate Session.UpdatedAt = UtcNow on EVERY read, which made ListAsync's
 * recency sort random — the same session reordered between calls. The stored
 * header now carries the real last-activity timestamp; reads never invent one.
 */

internal sealed record MessageEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("payload")] object Payload);
