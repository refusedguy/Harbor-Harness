namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     Per-test fixture: creates a unique temp <c>~/.harbor</c>-like directory with a
/// <c>plugins/</c> subdirectory. Disposes on test completion.
/// </summary>
public sealed class PluginTestFixture : IDisposable
{
    private readonly string _tempRoot;

    private PluginTestFixture(string tempRoot)
    {
        _tempRoot = tempRoot;
        HarborDir = Path.Combine(tempRoot, "harbor");
        PluginsDir = Path.Combine(HarborDir, "plugins");
        CacheDir = Path.Combine(PluginsDir, "cache");
        Directory.CreateDirectory(PluginsDir);
    }

    /// <summary>The synthetic <c>~/.harbor</c> directory root.</summary>
    public string HarborDir { get; }

    /// <summary>The synthetic <c>~/.harbor/plugins</c> directory.</summary>
    public string PluginsDir { get; }

    /// <summary>The synthetic <c>~/.harbor/plugins/cache</c> directory.</summary>
    public string CacheDir { get; }

    /// <summary>Create a new fixture under a unique temp path.</summary>
    public static Task<PluginTestFixture> CreateAsync(string? uniqueSuffix = null)
    {
        string suffix = string.IsNullOrEmpty(uniqueSuffix) ? string.Empty : "-" + uniqueSuffix;
        string root = Path.Combine(Path.GetTempPath(), "harbor-tests" + suffix + "-" + Guid.NewGuid().ToString("N"));
        var fixture = new PluginTestFixture(root);
        return Task.FromResult(fixture);
    }

    /// <summary>Write a plugin source file into <see cref="PluginsDir" />.</summary>
    public async Task WritePluginAsync(string source, string fileName = "plugin.cs")
    {
        string path = Path.Combine(PluginsDir, fileName);
        await File.WriteAllTextAsync(path, source).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }
}
