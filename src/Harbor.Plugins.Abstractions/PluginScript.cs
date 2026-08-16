using System.Security.Cryptography;
using System.Text;
namespace Harbor.Plugins.Abstractions;
/// <summary>
///     Immutable wrapper around a single CS-source plugin file: its on-disk path, raw source
///     text, and a deterministic SHA-256 content hash used for caching compiled assemblies.
/// </summary>
public sealed class PluginScript
{
    /// <summary>
    ///     Construct a new <see cref="PluginScript" /> from a file path.
    /// </summary>
    /// <param name="path">Absolute path to the .cs file.</param>
    /// <param name="source">Raw source text of the file (UTF-8 decoded).</param>
    public PluginScript(string path, string source)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Hash = ComputeHash(source);
    }

    /// <summary>The absolute on-disk path of the plugin source file.</summary>
    public string Path { get; }

    /// <summary>The raw source text of the plugin.</summary>
    public string Source { get; }

    /// <summary>
    ///     Lowercase hex SHA-256 of <see cref="Source" />. Used as the cache key for the
    ///     compiled assembly — files with the same hash skip recompilation on subsequent loads.
    /// </summary>
    public string Hash { get; }

    /// <summary>
    ///     Load a <see cref="PluginScript" /> from disk, reading the file as UTF-8.
    /// </summary>
    /// <param name="path">Absolute path to the .cs file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with the script wrapper, or failure if the file cannot be read.</returns>
    public static async Task<Result<PluginScript>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<PluginScript>("Plugin path cannot be empty.");

        if (!File.Exists(path))
            return Result.Failure<PluginScript>($"Plugin file not found: {path}");

        try
        {
            string source = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
            return Result.Success(new PluginScript(path, source));
        }
        catch (IOException ex)
        {
            return Result.Failure<PluginScript>($"Failed to read plugin '{path}': {ex.Message}");
        }
    }

    /// <summary>
    ///     SHA-256 of the source text, returned as lowercase hex. Used as cache key.
    /// </summary>
    private static string ComputeHash(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
