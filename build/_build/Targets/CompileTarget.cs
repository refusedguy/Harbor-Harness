using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Compile target — runs <c>dotnet build</c> against the entire solution
///     in the configured <see cref="BuildConfiguration" />. Uses
///     <c>--no-restore</c> (assumes <see cref="RestoreTarget" /> ran first).
///     Enables parallel build (<c>-m</c>) and binary log (<c>-bl</c>) for
///     diagnostic capture. Dry-run emits only the equivalent argv.
/// </summary>
public static class CompileTarget
{
    /// <summary>
    ///     The environment variable that names the file the compiler's warnings
    ///     are written to. Unset → no file logger, no analyzer, and the
    ///     compile behaves exactly as it behaved before.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         It exists for ONE caller: the CI build job, which runs the
    ///         unused-reference analyzer (ReferenceTrimmer, #1853) inside this
    ///         compile and needs the compiler's warnings in a FILE to compare
    ///         against the baseline. Before #2108 that answer came from a
    ///         second job, which compiled the whole tree again — 260 s, 22 % of
    ///         the pull-request lane's runner-minutes, for one gate.
    ///     </para>
    ///     <para>
    ///         The logger is MSBuild's own file logger, spelled the way the
    ///         deleted job spelled it: <c>-flp:logfile=&lt;path&gt;;warningsonly</c>.
    ///         It writes the compiler's warnings in the canonical
    ///         <c>…: warning RT0002: … [project]</c> shape the gate reads, and
    ///         it writes them whether or not the build fails — which is what
    ///         lets the gate report an answer it did not get.
    ///     </para>
    ///     <para>
    ///         The doc file is turned on with it, and for the same reason:
    ///         the analyzer says so itself (RT0000) — without the doc file its
    ///         "used references" answer is less accurate, because a reference
    ///         used only from a <c>&lt;see cref="…"/&gt;</c> is bound only
    ///         when the documentation is generated. It has to be a GLOBAL
    ///         property (<c>/p:</c>), because fourteen test projects turn the
    ///         doc file off in their own .csproj and a project-level
    ///         assignment wins over a props file.
    ///     </para>
    /// </remarks>
    private const string WarningLogVariable = "HARBOR_WARNING_LOG";

    /// <summary>
    ///     Executes <c>dotnet build</c> on the given solution using the
    ///     settings from <paramref name="settings" />.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings, BuildOutput output)
    {
        var configuration = settings.ConfigurationString;
        var solutionPath = solution.Path.ToString();
        var warningLog = Environment.GetEnvironmentVariable(WarningLogVariable);
        var scan = !string.IsNullOrWhiteSpace(warningLog);
        var extraArgs = scan
            ? new[]
            {
                "-p:GenerateDocumentationFile=true",
                $"-flp:logfile={warningLog};warningsonly"
            }
            : Array.Empty<string>();
        var argv = new List<string>
        {
            "dotnet", "build", solutionPath, "-c", configuration, "--no-restore",
            "-p:MaxCpuCount=0", "-p:ContinuousIntegrationBuild=true"
        };
        argv.AddRange(extraArgs);
        output.Cmd("Compile", argv);
        if (output.IsDryRun)
        {
            return;
        }
        DotNetTasks.DotNetBuild(s =>
        {
            var configured = s
                .SetProjectFile(solution)
                .SetConfiguration(configuration)
                .EnableNoRestore()
                // Same deterministic-build property the ci.yml build job passes,
                // so ./build.sh Compile and CI compile the identical graph.
                .SetProperty("ContinuousIntegrationBuild", "true")
                .SetProperty("MaxCpuCount", "0"); // 0 = use all CPUs (parallel build)
            return scan
                ? configured.SetProcessAdditionalArguments(extraArgs)
                : configured;
        });
    }
}
