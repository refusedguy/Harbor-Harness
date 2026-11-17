namespace Harbor.Build.Configuration;

public sealed class BuildSettings
{
    public BuildConfiguration Configuration { get; init; }
    public string TargetFramework { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;

    public string ConfigurationString => Configuration.ToString().ToLowerInvariant();
}
