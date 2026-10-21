using System;
using System.Collections.Generic;
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
    ///     The environment variable that carries ONE extra global property into
    ///     the build, as <c>Name=Value</c>.
    /// </summary>
    /// <remarks>
    ///     A global property is the only kind that beats a property the project
    ///     file sets UNCONDITIONALLY, and the difference is not a matter of
    ///     taste. MSBuild's own documentation says it in two sentences:
    ///
    ///     "If the project contains a property definition that has the same name
    ///     as an environment property, the property in the project overrides the
    ///     value of the environment variable." … "These global property values
    ///     override property values that are set in the project file. This
    ///     includes environment properties."
    ///
    ///     So an ENVIRONMENT variable named <c>DebugType</c> does NOT reach a
    ///     build whose <c>Directory.Build.props</c> says
    ///     <c>&lt;DebugType&gt;portable&lt;/DebugType&gt;</c>, and a lane that
    ///     believes it turned something off with an environment variable has
    ///     turned nothing off. The property has to travel on the command line,
    ///     where it is a global property.
    /// </remarks>
    private const string ExtraPropertyVariable = "HARBOR_BUILD_PROPERTY";

    /// <summary>
    ///     The extra global property the environment asks for, or
    ///     <c>null</c> when it asks for none (or asks for nonsense).
    /// </summary>
    private static (string Key, string Value)? ExtraProperty()
    {
        var raw = Environment.GetEnvironmentVariable(ExtraPropertyVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var separator = raw.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }
        var key = raw[..separator].Trim();
        var value = raw[(separator + 1)..].Trim();
        return key.Length == 0 ? null : (key, value);
    }

    /// <summary>
    ///     Executes <c>dotnet build</c> on the given solution using the
    ///     settings from <paramref name="settings" />.
    /// </summary>
    public static void Execute(Solution solution, BuildSettings settings, BuildOutput output)
    {
        var configuration = settings.ConfigurationString;
        var solutionPath = solution.Path.ToString();
        var extra = ExtraProperty();
        var argv = new List<string>
        {
            "dotnet",
            "build",
            solutionPath,
            "-c",
            configuration,
            "--no-restore",
            "-p:MaxCpuCount=0",
            "-p:ContinuousIntegrationBuild=true"
        };
        if (extra is not null)
        {
            argv.Add($"-p:{extra.Value.Key}={extra.Value.Value}");
        }
        output.Cmd("Compile", [.. argv]);
        if (output.IsDryRun)
        {
            return;
        }
        var build = s => s
            .SetProjectFile(solution)
            .SetConfiguration(configuration)
            .EnableNoRestore()
            // Same deterministic-build property the ci.yml build job passes,
            // so ./build.sh Compile and CI compile the identical graph.
            .SetProperty("ContinuousIntegrationBuild", "true")
            .SetProperty("MaxCpuCount", "0"); // 0 = use all CPUs (parallel build)
        if (extra is not null)
        {
            build = build.SetProperty(extra.Value.Key, extra.Value.Value);
        }
        DotNetTasks.DotNetBuild(build);
    }
}
