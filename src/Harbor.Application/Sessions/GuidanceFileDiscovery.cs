namespace Harbor.Application.Sessions;

/// <summary>
///     #2675 item 070: discovers the guidance files that always belong in the
///     system prompt. These are the context files in the session's working
///     directory and in every ancestor up to (and including) the user's home.
/// </summary>
/// <remarks>
///     <para>
///         The probe is deliberately existence-only and bounded: at most one
///         walk to the root and one check per name in <see cref="FileNames"/>
///         for every directory. The byte budget and the content fingerprint stay in
///         <see cref="WorkspaceContextService"/>, which calls this type for the
///         candidate list; discovery itself never reads file contents.
///     </para>
///     <para>
///         This slice covers the ancestor walk and the fallback names. Subtree
///         guidance loaded only after the agent reads a matching file is a
///         separate follow-up and is not implemented here.
///     </para>
/// </remarks>
public static class GuidanceFileDiscovery
{
    /// <summary>
    ///     Guidance file names in probe order. <c>AGENT.md</c> is the fallback
    ///     spelling; <c>CLAUDE.md</c> stays second for compatibility with the
    ///     pre-#2675 discovery order.
    /// </summary>
    public static readonly string[] FileNames = ["AGENTS.md", "CLAUDE.md", "AGENT.md"];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     A guidance file found by <see cref="Discover"/>.
    /// </summary>
    /// <param name="FullPath">Absolute path used to read the file.</param>
    /// <param name="Label">
    ///     Prompt label: the bare file name for the working directory and a
    ///     relative <c>../</c> path for ancestors, so the model can tell which
    ///     directory a rule came from.
    /// </param>
    public sealed record GuidanceFile(string FullPath, string Label);

    /// <summary>
    ///     Walk from <paramref name="workingDirectory"/> towards the root and
    ///     return every present guidance file, nearest directory first.
    ///     <paramref name="homeDirectory"/> bounds the walk when it is an
    ///     ancestor; when it is null or off the ancestor chain the walk ends at
    ///     the filesystem root.
    /// </summary>
    public static IReadOnlyList<GuidanceFile> Discover(string workingDirectory, string? homeDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Array.Empty<GuidanceFile>();

        string root = Path.GetFullPath(workingDirectory);
        string? stop = string.IsNullOrWhiteSpace(homeDirectory) ? null : Path.GetFullPath(homeDirectory);
        var found = new List<GuidanceFile>(FileNames.Length * 2);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        string? current = root;
        while (current is not null)
        {
            for (int i = 0; i < FileNames.Length; i++)
            {
                string name = FileNames[i];
                string full = Path.Combine(current, name);
                if (!File.Exists(full) || !seen.Add(full))
                    continue;

                string label = string.Equals(current, root, PathComparison)
                    ? name
                    : Path.GetRelativePath(root, full).Replace('\\', '/');
                found.Add(new GuidanceFile(full, label));
            }

            if (stop is not null && string.Equals(current, stop, PathComparison))
                break;

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, PathComparison))
                break;
            current = parent;
        }

        return found;
    }
}
