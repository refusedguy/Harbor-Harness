using System.Security.Cryptography;
using System.Text;
using Harbor.Abstractions.Filesystem;

namespace Harbor.Application.Filesystem;

/// <summary>
///     The on-disk layout of the tool-output spill area (issue #1748).
/// </summary>
/// <remarks>
///     <para>
///         <b>One owner for the path rules.</b> The spill area is addressed by
///         two independent parties — the store that writes into it and the tool
///         that reads out of it — and a path rule that exists in two places is a
///         path rule that will disagree with itself. Every name is composed
///         here, from parts that are NOT model input: the session id and the
///         tool call id.
///     </para>
///     <para>
///         <b>Why the names are hashed, not spelled.</b> A session id and a tool
///         call id are arbitrary strings. Spelling them into a path would put
///         separators, dots and Windows reserved names into the filesystem's
///         hands, so both are reduced to a digest before they touch it. The
///         session key keeps a readable prefix (a human finding the directory
///         by hand is a real case) and appends a digest of the whole id, so two
///         ids that share a prefix still get different directories.
///     </para>
/// </remarks>
internal static class ToolOutputSpillLayout
{
    /// <summary>The directory name under Harbor's home directory.</summary>
    internal const string DirectoryName = "spill";

    /// <summary>The extension of a spilled output file.</summary>
    internal const string FileExtension = ".txt";

    /// <summary>How much of the session id is kept readable in the directory name.</summary>
    private const int MaxReadableKeyLength = 32;

    /// <summary>
    ///     The default root of the spill area: <c>$HARBOR_HOME/spill</c>. It is
    ///     Harbor's PRIVATE directory, never the user's working tree.
    /// </summary>
    internal static string DefaultRoot =>
        Path.Combine(HarborPaths.GetHarborHome(), DirectoryName);

    /// <summary>
    ///     The directory name of one session's spill area.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>A filesystem-safe directory name.</returns>
    internal static string SessionKey(string sessionId)
    {
        string source = sessionId ?? string.Empty;
        var key = new StringBuilder(MaxReadableKeyLength + 17);
        int taken = 0;
        foreach (char c in source)
        {
            if (taken == MaxReadableKeyLength)
            {
                break;
            }

            key.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
            taken++;
        }

        if (taken == 0)
        {
            key.Append("session");
        }

        // The digest of the WHOLE id, so two ids that share a readable prefix
        // still land in different directories.
        key.Append('-').Append(Digest(source));
        return key.ToString();
    }

    /// <summary>
    ///     The directory holding one session's spilled outputs.
    /// </summary>
    /// <param name="root">The root of the spill area.</param>
    /// <param name="sessionId">The session id.</param>
    /// <returns>The absolute directory path.</returns>
    internal static string SessionDirectory(string root, string sessionId) =>
        Path.Combine(root, SessionKey(sessionId));

    /// <summary>
    ///     The file name of one tool call's spilled output.
    /// </summary>
    /// <param name="toolCallId">The tool call id.</param>
    /// <returns>A filesystem-safe file name.</returns>
    internal static string FileName(string toolCallId) =>
        Digest(toolCallId ?? string.Empty) + FileExtension;

    /// <summary>
    ///     The absolute path of one tool call's spilled output.
    /// </summary>
    /// <param name="root">The root of the spill area.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="toolCallId">The tool call id.</param>
    /// <returns>The absolute file path.</returns>
    internal static string PathFor(string root, string sessionId, string toolCallId) =>
        Path.Combine(SessionDirectory(root, sessionId), FileName(toolCallId));

    /// <summary>
    ///     Resolve a candidate path against one session's spill directory.
    /// </summary>
    /// <param name="root">The root of the spill area.</param>
    /// <param name="sessionId">The session the read belongs to.</param>
    /// <param name="candidatePath">The path the model asked for.</param>
    /// <param name="resolved">The canonical path, when the candidate is inside the session's directory.</param>
    /// <returns>
    ///     <see langword="true" /> when the candidate resolves inside the
    ///     session's spill directory. A path that escapes it — a traversal, an
    ///     absolute path elsewhere, a relative path — is refused.
    /// </returns>
    internal static bool TryResolve(string root, string sessionId, string candidatePath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string full;
        string directory;
        try
        {
            full = Path.GetFullPath(candidatePath);
            directory = Path.GetFullPath(SessionDirectory(root, sessionId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        // The prefix comparison is the confinement: the candidate must BE inside
        // the session's directory, not merely start with the same characters.
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!full.StartsWith(prefix, comparison))
        {
            return false;
        }

        resolved = full;
        return true;
    }

    /// <summary>
    ///     A short, stable digest of a value: 16 hex characters of SHA-256.
    /// </summary>
    /// <param name="value">The value to digest.</param>
    /// <returns>The digest.</returns>
    private static string Digest(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 8);
    }
}
