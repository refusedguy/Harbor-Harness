namespace Harbor.Application.Sessions;

/// <summary>
///     Run-directory layout for epic #42 (issue #1610): the single home for
///     <c>GetHarborHome</c> / <c>RunsRoot</c> / <c>RunDir</c>, extracted out of
///     <see cref="WorkspaceMaterializer" /> so the S3–S6 slices stop fanning
///     into that static hub for paths.
/// </summary>
/// <remarks>
///     <para>
///         The runs root is resolved exactly like
///         <c>Harbor.Hosting.HarborPaths.GetHarborHome()</c> (<c>HARBOR_HOME</c>
///         or <c>~/.harbor</c>) but WITHOUT referencing the Hosting project:
///         Application depends on Abstractions only, so the five lines live
///         here rather than behind a new <c>ProjectReference</c>.
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
