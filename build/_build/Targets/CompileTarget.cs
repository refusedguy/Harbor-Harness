using Harbor.Build.Meta;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     Compile target — runs <c>dotnet msbuild</c> against the entire solution
///     in the configured <see cref="BuildConfiguration" />. The restore is
///     NOT part of this command: <see cref="RestoreTarget" /> ran first
///     (Build.cs: <c>Compile =&gt; _.DependsOn(Restore)</c>), so the assets
///     file is in place. The node count and the node reuse are the real
///     MSBuild switches, not properties. Dry-run emits only the equivalent
///     argv.
/// </summary>
/// <remarks>
///     <para>
///         WHY <c>dotnet msbuild</c> AND NOT <c>dotnet build</c> (#2111).
///     </para>
///     <para>
///         The old line was <c>-p:MaxCpuCount=0</c>, and it was INERT:
///         MSBuild reads the node count as the SWITCH
///         <c>-maxcpucount</c>, and the <c>MaxCpuCount</c> PROPERTY appears in
///         no MSBuild target and in no global-property lookup. It looked
///         like "use all CPUs" and did nothing at all. The same is true of
///         <c>-p:MaxCpuCount=1</c>, which issue #1929 measured as
///         indistinguishable from the baseline (wall ratio 1.08).
///     </para>
///     <para>
///         <c>dotnet build</c> is <c>dotnet msbuild -restore -t:Build</c> plus
///         the SDK's own defaults, and the node count is one of the switches
///         only MSBuild's own command line carries. Going through
///         <c>dotnet msbuild</c> is what makes the switches reachable at all:
///         <see cref="DotNetMSBuildSettings" /> has <c>/maxcpucount</c>,
///         <c>/nodeReuse</c> and <c>/graphBuild</c>, and
///         <c>dotnet build</c> has no way to pass the last two.
///     </para>
///     <para>
///         THE TWO SWITCHES, AND WHAT EACH ONE IS FOR
///     </para>
///     <list type="bullet">
///         <item>
///             <c>/maxcpucount:{Environment.ProcessorCount}</c> — the real
///             node count. The value MUST be 1..1024: MSBuild's
///             <c>ProcessMaxCPUCountSwitch</c> throws
///             <c>InvalidMaxCPUCountValueOutsideRange</c> for anything
///             outside that range, so the old "0 means all CPUs" spelling
///             would have been a hard error here, not a no-op.
///         </item>
///         <item>
///             <c>/nodeReuse:false</c> — MSBuild nodes do not outlive the
///             build. This is what dotnet/arcade does
///             (<c>MSBUILDDISABLENODEREUSE=1</c>), and the reason is not
///             speed: a reused node keeps the previous build's state
///             (evaluation caches, loaded assemblies), so a build that
///             reuses a node is not the same build as a build that does
///             not. On CI there is nothing to reuse, so the switch costs
///             nothing and removes the variable.
///         </item>
///     </list>
///     <para>
///         <c>/graphBuild</c> is NOT here, and the reason is measured
///         rather than assumed (CI run 39634501061, three replicates per
///         arm, the same runner): the graph build WORKS on this solution
///         (118 compilations, exit 0) and it is 1.3x the wall of the same
///         build without it — 412.1 s against 304.2 s at the median, with
///         the compiler's own task time at 858 s against 266 s. The graph
///         is built once, but every project is then evaluated in a graph
///         node, and the analyzers run again. The arm is kept in the
///         harness, so the number can be re-read; the switch is not in
///         the build. See docs/BENCHMARKS.md §6.7.2.
///     </para>
///     <para>
///         The restore stays where it was: the <c>Restore</c> target runs
///         first (Build.cs: <c>Compile =&gt; _.DependsOn(Restore)</c>), so
///         <c>dotnet msbuild -t:Build</c> finds the assets file in place and
///         does not need <c>-restore</c>.
///     </para>
/// </remarks>
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
    ///         reaches <c>dotnet msbuild</c> unchanged: NUKE appends them to
    ///         the tool's own argument list, verbatim and unescaped. The
    ///         alternative — NUKE's own <c>Loggers</c> setting — renders
    ///         <c>/logger:{value}</c>, and MSBuild reads a single word there
    ///         as an ASSEMBLY name: the first merged run died with
    ///         "MSBUILD : error MSB1021: Cannot create an instance of the
    ///         logger flp. Could not load file or assembly flp".
    ///     </para>
    /// </remarks>
    private const string WarningLogVariable = "HARBOR_WARNING_LOG";

    /// <summary>
    ///     Executes <c>dotnet msbuild</c> on the given solution using the
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
        var maxCpuCount = Environment.ProcessorCount;
        // The SAME argv the tool below runs, switch for switch. The property
        // name is spelled the way MSBuild spells it — a typo here would
        // print a command that sets a property nobody reads, and the print
        // is the only record of what the build did.
        var argv = new List<string>
        {
            "dotnet", "msbuild", solutionPath, "/target:Build",
            "/property:Configuration=" + configuration,
            "/property:ContinuousIntegrationBuild=true",
            "/maxcpucount:" + maxCpuCount,
            "/nodeReuse:false"
        };
        // NUKE appends the additional arguments to the tool's own list, so
        // they belong at the END here too: the printed command is the
        // command that runs.
        argv.AddRange(extraArgs);
        output.Cmd("Compile", argv);
        if (output.IsDryRun)
        {
            return;
        }
        DotNetTasks.DotNetMSBuild(s =>
        {
            var configured = s
                .SetTargetPath(solutionPath)
                .SetTargets("Build")
                .SetConfiguration(configuration)
                // Same deterministic-build property the ci.yml build job passes,
                // so ./build.sh Compile and CI compile the identical graph.
                .SetProperty("ContinuousIntegrationBuild", "true")
                // The REAL switch. 1..1024 or MSBuild throws; the processor
                // count is what "use every core" means on this machine.
                .SetMaxCpuCount(maxCpuCount)
                // No node outlives the build (dotnet/arcade's
                // MSBUILDDISABLENODEREUSE=1, as a switch).
                .SetNodeReuse(false);
            return scan
                ? configured.SetProcessAdditionalArguments(extraArgs)
                : configured;
        });
    }
}
