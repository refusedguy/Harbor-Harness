namespace Harbor.Terminal.Abstractions.Plugins;

/// <summary>TEMPORARY red-direction probe for #916 — deleted in the next commit.</summary>
internal sealed class RedProbe : ITuiPlugin
{
    /// <inheritdoc />
    public string Name => "red-probe";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public string Description => "red probe";

    /// <inheritdoc />
    public void RegisterTui(Navigation.ViewRegistry views, Navigation.ViewModelRegistry viewModels)
    {
    }
}
