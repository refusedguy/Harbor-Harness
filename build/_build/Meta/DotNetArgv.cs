using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Meta;

/// <summary>
///     Renders a configured <see cref="DotNetPublishSettings" /> as the
///     equivalent <c>dotnet publish</c> command-line. Used by dry-run to
///     print exactly what would execute (same properties, same order of
///     flags) without spawning a process.
/// </summary>
public static class DotNetArgv
{
    /// <summary>Builds the argv array for the given publish settings.</summary>
    public static IReadOnlyList<string> RenderPublish(DotNetPublishSettings settings)
    {
        var argv = new List<string> { "dotnet", "publish" };
        string project = settings.Project;
        if (!string.IsNullOrEmpty(project))
        {
            argv.Add(project);
        }
        string configuration = settings.Configuration;
        if (!string.IsNullOrEmpty(configuration))
        {
            argv.Add("-c");
            argv.Add(configuration.ToLowerInvariant());
        }
        string framework = settings.Framework;
        if (!string.IsNullOrEmpty(framework))
        {
            argv.Add("-f");
            argv.Add(framework);
        }
        string runtime = settings.Runtime;
        if (!string.IsNullOrEmpty(runtime))
        {
            argv.Add("-r");
            argv.Add(runtime);
        }
        if (settings.SelfContained.HasValue)
        {
            argv.Add("--self-contained");
            argv.Add(settings.SelfContained.Value ? "true" : "false");
        }
        if (settings.NoBuild == true)
        {
            argv.Add("--no-build");
        }
        if (settings.NoRestore == true)
        {
            argv.Add("--no-restore");
        }
        string output = settings.Output;
        if (!string.IsNullOrEmpty(output))
        {
            argv.Add("-o");
            argv.Add(output);
        }
        if (settings.Properties is not null)
        {
            foreach ((string name, object value) in settings.Properties)
            {
                argv.Add($"-p:{name}={value}");
            }
        }
        return argv;
    }
}
