namespace Harbor.Application.Sessions;

/// <summary>
///     Workspace path resolution (epic #42, decomp #1610): the Harbor home
///     directory, the runs root beneath it, and the per-run directory.
///     Extracted from <see cref="WorkspaceMaterializer" /> so the S3–S8
///     slices (freeze, checks, report, accept, reject) resolve paths without
///     referencing the materializer hub — and so the <c>CheckRunner</c>
///     duplicate (<c>GetHarborHome</c>/<c>RunsRoot</c>/<c>RunDir</c>) has a
///     single home.
/// </summary>
/// <remarks>
///     <para>
///         Resolution mirrors <c>Harbor.Hosting.HarborPaths.GetHarborHome()</c>
///         (<c>HARBOR_HOME</c> or <c>~/.harbor</c>) but WITHOUT referencing the
///         Hosting project: Application depends on Abstractions only, so the
///         five lines live here rather than behind a new
///         <c>ProjectReference</c>.
///     </para>
/// </remarks>
public interface IWorkspacePaths
{
    /// <summary>Resolve the Harbor home directory (<c>HARBOR_HOME</c> or <c>~/.harbor</c>).</summary>
    string GetHarborHome();

    /// <summary>Resolve the runs root (<c>&lt;home&gt;/runs</c>).</summary>
    string RunsRoot();

    /// <summary>Resolve the directory of one run (<c>&lt;runs&gt;/&lt;runId&gt;</c>).</summary>
    /// <param name="runId">Run id (directory name; validated by callers, not here).</param>
    string RunDir(string runId);
}

/// <summary>
///     Default <see cref="IWorkspacePaths" />: the five-line
///     <c>HARBOR_HOME</c>-or-<c>~/.harbor</c> resolution previously duplicated
///     in <see cref="WorkspaceMaterializer" /> and <c>CheckRunner</c>.
/// </summary>
public sealed class WorkspacePaths : IWorkspacePaths
{
    /// <summary>Shared production instance (the static slices resolve paths through it).</summary>
    public static IWorkspacePaths Default { get; } = new WorkspacePaths();

    private WorkspacePaths()
    {
    }

    /// <inheritdoc />
    public string GetHarborHome()
    {
        if (Environment.GetEnvironmentVariable("HARBOR_HOME") is { Length: > 0 } custom)
            return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
    }

    /// <inheritdoc />
    public string RunsRoot() => Path.Combine(GetHarborHome(), "runs");

    /// <inheritdoc />
    public string RunDir(string runId) => Path.Combine(RunsRoot(), runId);
}
