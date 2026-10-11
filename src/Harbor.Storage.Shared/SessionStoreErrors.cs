// SessionStoreErrors.cs — shared failure shapes for the file-backed session
// stores (Harbor.Storage.Jsonl, Harbor.Storage.Sqlite).
//
// Linked source (no .csproj): compiled into each store assembly via
// <Compile Include="..\Harbor.Storage.Shared\*.cs" /> — the same mechanism as
// Harbor.Providers.Shared. No <ProjectReference> is added, so the
// Storage_ReferencesOnlyAbstractions architecture rule stays green.
//
// Every factory returns the exact string the stores produced before the #184
// split (pinned by JsonlSessionStoreRopTests, SqliteSessionStoreRopTests and
// MemorySessionStoreRopTests) — change a shape here and all three suites fail.

using System;

namespace Harbor.Storage.Shared;

/// <summary>
///     Canonical <see cref="Result" /> failure texts for session stores.
///     The "not found" shapes are identical across Jsonl/Sqlite/Memory
///     (asserted by the #199 ROP acceptance suites); centralizing them keeps
///     the three stores from drifting apart one literal at a time.
/// </summary>
internal static class SessionStoreErrors
{
    /// <summary>Missing session: <c>Session '{id}' not found.</c></summary>
    public static string SessionNotFound(string sessionId) =>
        $"Session '{sessionId}' not found.";

    /// <summary>Missing message: <c>Message '{mid}' not found in session '{sid}'.</c></summary>
    public static string MessageNotFound(string sessionId, string messageId) =>
        $"Message '{messageId}' not found in session '{sessionId}'.";

    /// <summary>Missing checkpoint: <c>Checkpoint '{cid}' not found in session '{sid}'.</c></summary>
    public static string CheckpointNotFound(string sessionId, string checkpointId) =>
        $"Checkpoint '{checkpointId}' not found in session '{sessionId}'.";

    /// <summary>Caller-supplied id that must never reach <c>File.*</c> (#83).</summary>
    public static string InvalidSessionId(string? sessionId) =>
        $"Invalid session id '{sessionId}'.";
}
