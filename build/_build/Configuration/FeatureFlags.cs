namespace Harbor.Build.Configuration;

public sealed class FeatureFlags
{
    public bool WithPlugins { get; init; }
    public bool WithScripting { get; init; }
    public bool WithSpectreTui { get; init; }
    public bool WithAllProviders { get; init; }
    public bool WithAllTools { get; init; }
    public bool Minimal { get; init; }

    public FeatureFlags Resolved() => this;

    public bool IsAotCompatible => !WithPlugins && !WithScripting && !WithSpectreTui;

    public override string ToString()
    {
        return $"plugins={WithPlugins}, scripting={WithScripting}, spectre={WithSpectreTui}, providers={WithAllProviders}, tools={WithAllTools}, minimal={Minimal}";
    }
}
