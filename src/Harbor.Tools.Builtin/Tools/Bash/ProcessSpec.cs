namespace Harbor.Tools.Builtin;

/// <summary>
///     The fully-resolved description of a child process to launch (#1590).
///     BashTool translates its JSON arguments into a <see cref="ProcessSpec" />
///     and hands it to <see cref="IProcessProxy" />; the OS-process machinery
///     (`System.Diagnostics.Process`, pipes, kill) lives entirely behind the
///     proxy, so tests can substitute a script without <c>/bin/bash</c>.
/// </summary>
public sealed record ProcessSpec
{
    /// <summary>The executable to run (the tool's shell: cmd.exe / /bin/bash).</summary>
    public required string FileName { get; init; }

    /// <summary>Argument list passed verbatim to the child (no re-quoting).</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory the child starts in; validated to exist by the caller.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    ///     Entries merged over the inherited environment (last wins); <c>null</c>
    ///     means "inherit everything unchanged".
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
