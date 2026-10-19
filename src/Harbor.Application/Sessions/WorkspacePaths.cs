namespace Harbor.Application.Sessions;

using Harbor.Abstractions.Env;

/// <summary>
///     Run-directory layout for epic #42 (issue #1610): the single home for
///     <c>GetHarborHome</c> / <c>RunsRoot</c> / <c>RunDir</c>, extracted out of
///     <see cref="WorkspaceMaterializer" /> so the S3–S6 slices stop fanning
///     into that static hub for paths.
/// </summary>
/// <remarks>
///     <para>
///         The runs root is resolved through
///         <see cref="Harbor.Abstractions.Filesystem.HarborPaths.GetHarborHome" />
///         (<c>HARBOR_HOME</c> or <c>~/.harbor</c>) — the same helper
///         Harbor.Hosting forwards to (#1570). Application depends on
///         Abstractions only, which is exactly why the helper now lives there.
///     </para>
///     <para>
///         Consumers use <see cref="WorkspacePaths.Default" /> (the process-wide
///         layout). The interface exists so tests and future layouts can
///         substitute without touching every call site.
///     </para>
/// </remarks>
public interface IWorkspacePaths
{
    /// <summary>
    ///     Resolve the Harbor home directory: <c>HARBOR_HOME</c> when set to a
    ///     non-empty value, else <c>~/.harbor</c> under the user profile.
    /// </summary>
    string GetHarborHome();

    /// <summary>Root holding every run directory (<c>$HARBOR_HOME/runs</c>).</summary>
    string RunsRoot();

    /// <summary>Directory for one run (<c>$HARBOR_HOME/runs/&lt;runId&gt;</c>).</summary>
    string RunDir(string runId);
}

/// <summary>Default <see cref="IWorkspacePaths" />: the on-disk layout above.</summary>
public sealed class WorkspacePaths : IWorkspacePaths
{
    /// <summary>Process-wide layout used by the S2–S6 slices.</summary>
    public static readonly IWorkspacePaths Default = new WorkspacePaths();

    private WorkspacePaths()
    {
    }

    /// <inheritdoc />
    public string GetHarborHome() => Harbor.Abstractions.Filesystem.HarborPaths.GetHarborHome();


    /// <inheritdoc />
    public string RunsRoot() => Path.Combine(GetHarborHome(), "runs");

    /// <inheritdoc />
    public string RunDir(string runId) => Path.Combine(RunsRoot(), runId);
}
