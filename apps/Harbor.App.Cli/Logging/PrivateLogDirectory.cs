namespace Harbor.App.Cli.Logging;

/// <summary>
///     Opens the directory that holds the CLI's per-run log files without
///     trusting whatever is already at that path.
/// </summary>
/// <remarks>
///     <para>
///         Harbor log lines carry full prompts and tool arguments, so the log
///         directory is a boundary: a symlink planted there redirects every
///         later write, and a group/world-readable directory exposes the
///         transcript to other local users (issue #2678 item 114).
///         <see cref="Directory.CreateDirectory(string)" /> does neither check.
///     </para>
///     <para>
///         Two guards run before a single byte is written.
///         <see cref="FileSystemInfo.LinkTarget" /> rejects a symlink without
///         requiring the path to exist yet;
///         <see cref="File.GetUnixFileMode" /> tightens an existing directory to
///         owner-only. A directory created here starts at 0700 on Unix.
///     </para>
/// </remarks>
internal static class PrivateLogDirectory
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode NonOwnerBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    ///     Ensure <paramref name="path"/> is a real, owner-only directory and
    ///     return it unchanged.
    /// </summary>
    /// <param name="path">Absolute path to the log directory.</param>
    /// <returns>The same path, now safe to open log files under.</returns>
    /// <exception cref="IOException">The path is a symlink.</exception>
    /// <exception cref="UnauthorizedAccessException">
    ///     The existing directory's permissions could not be tightened.
    /// </exception>
    public static string Ensure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        RejectIfSymlink(path);

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            RejectIfSymlink(path);
            return path;
        }

        Directory.CreateDirectory(path, OwnerOnly);

        // Close the window between the first check and the create: someone
        // could have planted a link at the path in the meantime.
        RejectIfSymlink(path);

        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & NonOwnerBits) != 0)
        {
            // An older install may have left this directory 0755. Tighten it
            // rather than accept it; if another user owns it, this throws and
            // the provider refuses to start, which is the fail-closed outcome.
            File.SetUnixFileMode(path, OwnerOnly);
        }

        return path;
    }

    private static void RejectIfSymlink(string path)
    {
        // LinkTarget does not stat the target, so it works before the directory
        // exists and for a dangling link alike.
        string? link = new DirectoryInfo(path).LinkTarget;
        if (link is not null)
        {
            throw new IOException($"Refusing to write logs through symlink '{path}' -> '{link}'.");
        }
    }
}
