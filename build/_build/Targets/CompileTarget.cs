using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
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
    ///         property (<c>-p:</c>), and the reason is measured, not
    ///         guessed: fourteen test projects turn the doc file off in their
    ///         own <c>.csproj</c>, and a props file does not beat a project
    ///         file — the first merged run carried the doc file in the props
    ///         and answered with fourteen RT-DOC-OFF findings and five
    ///         baseline rows the tree no longer produced. A global property
    ///         does beat it: MSBuild ignores a project-level assignment of a
    ///         property that was set globally ("Global properties cannot be
    ///         overridden. We silently ignore them if we try.").
    ///     </para>
    ///     <para>
    ///         Both arguments go in as RAW MSBuild arguments, through
    ///         <c>SetProcessAdditionalArguments</c>, because that is what
    ///         reaches <c>dotnet build</c> unchanged. The alternative —
    ///         NUKE's own <c>Loggers</c> setting — renders
    ///         <c>/logger:{value}</c>, and MSBuild reads a single word there
    ///         as an ASSEMBLY name: the first merged run died with
    ///         "MSBUILD : error MSB1021: Cannot create an instance of the
    ///         logger flp. Could not load file or assembly flp".
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
        // The file logger's own spelling: `-flp:logfile=<path>;warningsonly`.
        // The path is quoted when it carries a space, because NUKE appends
        // these arguments to the command line verbatim and does not quote
        // them — a path with a space would otherwise be split in two and the
        // logger would write to a file whose name is half a path.
        var fileLoggerArgument = warningLog is null
            ? null
            : warningLog.Contains(' ')
                ? $"-flp:\"logfile={warningLog};warningsonly\""
                : $"-flp:logfile={warningLog};warningsonly";
        var extraArgs = scan
            ? new[]
            {
                // The doc file, as a GLOBAL property. See the remark above:
                // a props file does not beat a project file, a global
                // property does.
                "-p:GenerateDocumentationFile=true",
                fileLoggerArgument!
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
