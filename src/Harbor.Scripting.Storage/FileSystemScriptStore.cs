// Storage layer — filesystem-backed script store. See IScriptStore.cs for layering rules.
namespace Harbor.Scripting.Storage;
/// <summary>
///     <see cref="IScriptStore" /> backed by a local filesystem directory.
/// </summary>
/// <remarks>
///     <para>
///         Discovers <c>.js</c> and <c>.ts</c> files in the configured
///         directory (non-recursive). The store is read/write — files are
///         created on <see cref="WriteAsync" /> and deleted on
///         <see cref="DeleteAsync" />. The directory is created on first
///         access if missing.
///     </para>
///     <para>
///         Multiple directories can be layered by passing several paths to
///         the constructor; later paths take precedence on write, and the
///         first match wins on read. This mirrors <c>~/.harbor/scripts/</c>
///         (user-global) + <c>&lt;project&gt;/.harbor/scripts/</c>
///         (project-local) discovery.
///     </para>
/// </remarks>
public sealed class FileSystemScriptStore : IScriptStore
{
    private static readonly string[] Extensions = [".js", ".ts", ".mjs", ".mts"];
    private readonly bool _createRoots;
    private readonly string[] _roots;

    /// <summary>
    ///     Construct a filesystem store over one or more root directories.
    /// </summary>
    /// <param name="roots">Root directories, in lookup-priority order (first wins).</param>
    /// <param name="createRoots">If <c>true</c>, missing root directories are created on first access.</param>
    public FileSystemScriptStore(IEnumerable<string> roots, bool createRoots = true)
    {
        var list = roots?.ToList();
        if (list is null || list.Count == 0)
        {
            throw new ArgumentException("At least one root directory is required.", nameof(roots));
        }
        _roots = list.ToArray();
        _createRoots = createRoots;
    }

    /// <summary>
    ///     Construct a single-root store.
    /// </summary>
    public FileSystemScriptStore(string root, bool createRoot = true)
        : this([root], createRoot)
    {
    }

    /// <summary>
    ///     Default user-global script directory: <c>~/.harbor/scripts/</c>.
    /// </summary>
    public static string DefaultUserDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "scripts");

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ScriptEntry>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<ScriptEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                if (_createRoots)
                {
                    try { Directory.CreateDirectory(root); }
                    catch
                    { /* ignore; surface on read */
                    }
                }
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                return Task.FromResult(Result.Failure<IReadOnlyList<ScriptEntry>>($"Failed to enumerate '{root}': {ex.Message}"));
            }

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(file);
                if (!IsScriptExtension(ext))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(file);
                if (!seen.Add(name))
                {
                    continue; // first root wins; later roots shadowed
                }

                var read = ReadFile(file, name);
                if (read.IsSuccess)
                {
                    entries.Add(read.Value);
                }
            }
        }

        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return Task.FromResult(Result.Success<IReadOnlyList<ScriptEntry>>(entries));
    }

    /// <inheritdoc />
    public Task<Result<ScriptEntry>> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        foreach (string root in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string ext in Extensions)
            {
                string path = Path.Combine(root, name + ext);
                if (File.Exists(path))
                {
                    var read = ReadFile(path, name);
                    return Task.FromResult(read);
                }
            }
        }
        return Task.FromResult(Result.Failure<ScriptEntry>($"Script '{name}' not found in any of {string.Join(", ", _roots)}."));
    }

    /// <inheritdoc />
    public Task<Result> WriteAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(Result.Failure("Script name is empty."));
        }

        string root = _roots[0];
        try
        {
            if (_createRoots)
            {
                Directory.CreateDirectory(root);
            }
            else if (!Directory.Exists(root))
            {
                return Task.FromResult(Result.Failure($"Root directory does not exist: '{root}'."));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Failed to ensure root '{root}': {ex.Message}"));
        }

        // Write to the first existing match, or the first root if creating new.
        string target = FindExisting(name) ?? Path.Combine(root, name + ".ts");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(target, content);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Failed to write '{target}': {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        string? existing = FindExisting(name);
        if (existing is null)
        {
            return Task.FromResult(Result.Failure($"Script '{name}' not found."));
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(existing);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Failure($"Failed to delete '{existing}': {ex.Message}"));
        }
    }

    private static bool IsScriptExtension(string ext) =>
        ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".mts", StringComparison.OrdinalIgnoreCase);

    private static Result<ScriptEntry> ReadFile(string path, string name)
    {
        try
        {
            string content = File.ReadAllText(path);
            var info = new FileInfo(path);
            string hash = HashContent(content);
            return Result.Success(new ScriptEntry(name, path, content, hash, info.LastWriteTimeUtc));
        }
        catch (Exception ex)
        {
            return Result.Failure<ScriptEntry>($"Failed to read '{path}': {ex.Message}");
        }
    }

    private static string HashContent(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private string? FindExisting(string name)
    {
        foreach (string root in _roots)
        {
            foreach (string ext in Extensions)
            {
                string path = Path.Combine(root, name + ext);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }
}
