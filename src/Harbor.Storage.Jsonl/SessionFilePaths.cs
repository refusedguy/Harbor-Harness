// SessionFilePaths.cs — session-id guard + file layout for the JSONL store.
//
// Extracted verbatim from JsonlSessionStore.cs (#184 god-object
// decomposition). All path resolution funnels through
// <see cref="TryResolveSessionFile" /> so caller-supplied ids can never
// escape the store root.

using Harbor.Storage.Shared;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     File layout + id validation for JSONL session files. Each session is
///     one <c>{sessionId}.jsonl</c> file directly under the store root.
/// </summary>
internal static class SessionFilePaths
{
    internal static string GetSessionFilePath(string rootDirectory, string sessionId) =>
        Path.Combine(rootDirectory, $"{sessionId}.jsonl");

    /// <summary>
    ///     Issue #83: session ids come from callers/CLI and must never escape
    ///     the store root. Only <c>[A-Za-z0-9_-]</c> (max 128 chars) is
    ///     accepted — everything else (<c>../</c>, absolute paths, separators)
    ///     fails before any <c>File.*</c> call. Fresh ids from
    ///     <see cref="Session.Create" /> (Guid "N") always satisfy this.
    /// </summary>
    internal static bool IsValidSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 128)
            return false;
        for (int i = 0; i < sessionId.Length; i++)
        {
            char c = sessionId[i];
            bool ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_';
            if (!ok)
                return false;
        }
        return true;
    }

    internal static Result<string> TryResolveSessionFile(string rootDirectory, string? sessionId)
    {
        if (!IsValidSessionId(sessionId))
            return Result.Failure<string>(SessionStoreErrors.InvalidSessionId(sessionId));
        return Result.Success(GetSessionFilePath(rootDirectory, sessionId!));
    }
}
