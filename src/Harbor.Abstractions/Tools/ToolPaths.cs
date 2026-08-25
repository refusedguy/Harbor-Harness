using System.IO;

namespace Harbor.Abstractions.Tools;

/// <summary>
///     Canonical path resolution for filesystem tools (ROP-A П.11): one place
///     owns the relative→absolute mapping and the "Invalid path" contract.
///     Previously five tools hand-copied this try/catch and two (read, ls)
///     had no guard at all — a malformed path escaped as a raw exception.
/// </summary>
public static class ToolPaths
{
    /// <summary>
    ///     Resolve <paramref name="rawPath" /> against the current directory
    ///     (relative or dot-prefixed paths) and normalize it. Failure carries
    ///     the canonical "Invalid path" message.
    /// </summary>
    public static Result<string> Resolve(string rawPath) =>
        Result.Success(rawPath)
            .MapTry(static p => p.StartsWith('.') || !Path.IsPathRooted(p)
                    ? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, p))
                    : Path.GetFullPath(p),
                ex => $"Invalid path: {ex.Message}");
}
