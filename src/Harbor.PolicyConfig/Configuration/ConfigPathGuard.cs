// ConfigPathGuard.cs: #2679 F020. The classify half of config-path protection.
// An "always allow" decision must never be persisted for a path that writes
// Harbor's own configuration or settings. Pure over its string input: no file
// system access, no environment read, no config store and no product wiring.
using System.Collections.Frozen;

namespace Harbor.Application.Configuration;

/// <summary>
///     Why a path was classified as protected from a permanent Allow (#2679 F020).
/// </summary>
public enum ConfigPathProtection
{
    /// <summary>The path is not one of Harbor's own settings.</summary>
    None,

    /// <summary>The path lies under a <c>.harbor</c> settings directory.</summary>
    HarborSettingsDirectory,

    /// <summary>The path names a known Harbor config file in the current directory.</summary>
    KnownConfigFileName,

    /// <summary>The path walks up with a <c>..</c> segment and leaves its directory.</summary>
    DirectoryEscape,
}

/// <summary>
///     Detects paths that point at Harbor's own configuration (#2679 F020).
/// </summary>
/// <remarks>
///     <para>
///         The rule a future wiring enforces is: a write to a protected path may
///         be approved for this session only, never saved as an "always" rule.
///         Persisting such an approval would let a tool rewrite the very rules
///         and credentials that gate it, which is the escalation this guard
///         exists to prevent.
///     </para>
///     <para>
///         Two shapes count. A <c>.harbor</c> path segment covers both the global
///         settings directory and a project-local one, including the plugins,
///         skills and hooks installed there. A bare known config file name covers
///         the short spelling a user or an agent actually types. A file with a
///         real directory component that is not Harbor's own, such as
///         <c>src/config.json</c>, is deliberately not protected: forcing "once"
///         on every application config in a repository would train users to
///         ignore the distinction.
///     </para>
///     <para>
///         The classifier is pure, stateless and thread-safe. It parses the
///         string with both separator styles and does not resolve <c>~</c>,
///         symlinks or the current directory, so the same input always yields
///         the same answer on every platform.
///     </para>
/// </remarks>
public static class ConfigPathGuard
{
    /// <summary>The directory name Harbor uses for global and project settings.</summary>
    public const string SettingsDirectoryName = ".harbor";

    private static readonly FrozenSet<string> KnownConfigFileNames = new[]
    {
        "config.json",
        "config.jsonc",
        "tui.json",
        "hooks.json",
        "mcp.json",
        "hosts.json",
        "daemon.psk",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns why <paramref name="path" /> is protected, or
    ///     <see cref="ConfigPathProtection.None" /> when it is an ordinary path.
    /// </summary>
    /// <param name="path">A path spelled by a user, an agent or a config store.</param>
    public static ConfigPathProtection Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ConfigPathProtection.None;

        string value = path.Trim();
        if (HasSegment(value, SettingsDirectoryName)) return ConfigPathProtection.HarborSettingsDirectory;
        if (!HasDirectorySegment(value) && KnownConfigFileNames.Contains(LastSegment(value))) return ConfigPathProtection.KnownConfigFileName;

        return ConfigPathProtection.None;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when an approval for this path must be
    ///     scoped to the current session.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    public static bool IsProtected(string? path) => Classify(path) != ConfigPathProtection.None;

    /// <summary>
    ///     Returns <see langword="true" /> when an approval for this path may be
    ///     saved as an "always" rule. The exact inverse of <see cref="IsProtected" />.
    /// </summary>
    /// <param name="path">The path to classify.</param>
    public static bool CanPersistAlways(string? path) => !IsProtected(path);

    /// <summary>
    ///     Returns <see cref="ConfigPathProtection.DirectoryEscape" /> when the
    ///     path contains a parent-directory (<c>..</c>) segment, or
    ///     <see cref="ConfigPathProtection.None" /> otherwise.
    /// </summary>
    /// <remarks>
    ///     This is the containment half of config-path protection, for callers
    ///     that are about to touch the file system. A config store accepts an
    ///     explicit path override; a <c>..</c> segment in that override lets the
    ///     caller step outside the directory the path names, so it must be
    ///     refused before any read or write. Unlike <see cref="Classify" /> this
    ///     check never reports <see cref="ConfigPathProtection.HarborSettingsDirectory" />
    ///     or <see cref="ConfigPathProtection.KnownConfigFileName" />: those mark
    ///     Harbor's own settings, which a config store is expected to manage.
    ///     The check is pure and does not resolve symlinks or the working
    ///     directory.
    /// </remarks>
    /// <param name="path">The config path a caller wants to read or write.</param>
    public static ConfigPathProtection ClassifyContainment(string? path)
        => HasParentSegment(path) ? ConfigPathProtection.DirectoryEscape : ConfigPathProtection.None;

    private static bool HasParentSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string value = path.Trim();
        int segmentStart = 0;
        for (int i = 0; i <= value.Length; i++)
        {
            bool isSeparator = i == value.Length || value[i] == '/' || value[i] == '\\';
            if (!isSeparator)
                continue;

            if (i - segmentStart == 2 && value[segmentStart] == '.' && value[segmentStart + 1] == '.')
                return true;

            segmentStart = i + 1;
        }
        return false;
    }

    private static bool HasSegment(string path, string segment)
    {
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '/' && path[i] != '\\') continue;
            int length = i - start;
            if (length == segment.Length && string.Compare(path, start, segment, 0, length, StringComparison.OrdinalIgnoreCase) == 0) return true;
            start = i + 1;
        }
        return false;
    }

    private static bool HasDirectorySegment(string path)
    {
        int last = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        if (last < 0) return false;

        int start = 0;
        for (int i = 0; i <= last; i++)
        {
            if (i < last && path[i] != '/' && path[i] != '\\') continue;
            int length = i - start;
            if (length > 0 && !(length == 1 && path[start] == '.')) return true;
            start = i + 1;
        }
        return false;
    }

    private static string LastSegment(string path)
    {
        int last = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return last < 0 ? path : path[(last + 1)..];
    }
}
