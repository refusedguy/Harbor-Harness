using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Sessions;

/// <summary>
///     Discovers the workspace inputs of <see cref="SystemPromptContext" />:
///     project context files (<c>AGENTS.md</c>, <c>CLAUDE.md</c>) from the
///     session's working directory and skill files (<c>*.md</c>) from
///     <c>&lt;workdir&gt;/.harbor/skills</c> plus <c>~/.harbor/skills</c>
///     (ROP-C Z3 — the prompt builder rendered those sections, but the loop
///     hardcoded empty arrays).
/// </summary>
/// <remarks>
///     <para>
///         #1523: registered in the composition root as the singleton
///         <see cref="IWorkspaceContextSource" /> — the snapshot cache lives
///         on the instance, so its lifetime and invariants are declared in
///         one place and tests isolate via a fresh instance.
///     </para>
///     <para>
///         All lookups are existence-tolerant: a missing directory or file
///         yields an empty result, never a failure — a workspace without skills
///         is normal, not an error.
///     </para>
///     <para>
///         Skill format: <c>&lt;name&gt;/SKILL.md</c> in a skills root
///         (preferred — front-matter <c>name:</c>, else the directory name) or
///         a legacy flat <c>&lt;name&gt;.md</c> file. The description comes
///         from a front-matter <c>description:</c> line when present, otherwise
///         from the first non-empty line stripped of Markdown markers.
///         Project-local skills shadow global ones with the same name.
///     </para>
///     <para>
///         Per-turn cost is bounded: two directory enumerations, a content
///         fingerprint of at most <see cref="MaxContextBytes" /> bytes per
///         context file, and the first few lines of every skill file. The
///         expensive prompt assembly itself stays behind
///         <c>CachingSystemPromptBuilder</c>'s content hash.
///     </para>
/// </remarks>
public sealed class WorkspaceContextService : IWorkspaceContextSource
{
    /// <summary>Project context files probed in the working directory.</summary>
    public static readonly string[] ContextFileNames = ["AGENTS.md", "CLAUDE.md"];

    private const string ProjectSkillsDir = ".harbor/skills";
    private const string SkillFileName = "SKILL.md";
    private const int MaxDescriptionLength = 160;

    /// <summary>
    ///     Total byte budget shared by <em>all</em> project context files (#2230).
    ///     The old per-file 256 KiB valve did not bound the prompt at all: two
    ///     files under the cap contributed 512 KiB. Files are added in probed
    ///     order and the file that would cross the budget is cut to the bytes
    ///     that remain; later files are skipped with a warning.
    /// </summary>
    public const int MaxContextBytes = 32 * 1024;

    private static readonly byte[] Utf8Bom = Encoding.UTF8.GetPreamble();
    private static readonly byte[] Separator = [0];

    private sealed record CachedSnapshot(
        IReadOnlyList<ContextFile> Files,
        IReadOnlyList<SkillDescriptor> Skills,
        long Version);

    private readonly ConcurrentDictionary<string, CachedSnapshot> _cache = new(StringComparer.Ordinal);
    private ILogger<WorkspaceContextService>? _logger;

    /// <summary>
    ///     Create a workspace source. The logger is optional: the static migration
    ///     facade and tests that only assert discovery do not need it, while the
    ///     composition root supplies one so truncation warnings reach the log.
    /// </summary>
    public WorkspaceContextService(ILogger<WorkspaceContextService>? logger = null) => _logger = logger;

    /// <summary>
    ///     Give the shared facade instance its host logger once, at composition
    ///     time. The static facade owns one process-wide instance so DI and
    ///     pre-DI callers share a cache; attaching here keeps that identity while
    ///     letting budget warnings reach the log (#2230).
    /// </summary>
    public void SetLogger(ILogger<WorkspaceContextService> logger) => _logger = logger;

    /// <summary>
    ///     Cached variant of the two per-turn loads. The version is a SHA-256
    ///     fingerprint of the context files' <em>contents</em> plus the two skill
    ///     directories' last-write ticks. Contents must be in the key because the
    ///     shared budget truncates by byte count and total: a same-length edit or
    ///     a change to one file that changes another file's cut still changes the
    ///     prompt, so a timestamp-only key would serve a stale snapshot (#2230).
    ///     Fast path reuses the cached lists without enumerating skills.
    /// </summary>
    public (IReadOnlyList<ContextFile> Files, IReadOnlyList<SkillDescriptor> Skills) GetOrLoadCached(string workingDirectory)
    {
        long version = ComputeVersion(workingDirectory);

        if (_cache.TryGetValue(workingDirectory, out var snap) && snap.Version == version)
            return (snap.Files, snap.Skills);

        var files = LoadContextFiles(workingDirectory);
        var skills = LoadSkills(workingDirectory);
        var entry = new CachedSnapshot(files, skills, version);
        _cache[workingDirectory] = entry;
        return (files, skills);
    }

    /// <summary>
    ///     Content-derived snapshot version. Context-file bytes (and their
    ///     lengths) feed the hash so any edit that changes what the budget embeds
    ///     invalidates the cache. Only the first <see cref="MaxContextBytes" />
    ///     bytes of a file can ever reach the prompt, so the hash is bounded by
    ///     the same budget even for an oversized file on disk.
    /// </summary>
    private long ComputeVersion(string workingDirectory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        try
        {
            foreach (string name in ContextFileNames)
            {
                AppendChars(hash, name);
                hash.AppendData(Separator); // field separator
                string path = Path.Combine(workingDirectory, name);
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    AppendLong(hash, stream.Length, scratch);
                    byte[] buffer = new byte[(int)Math.Min(stream.Length, MaxContextBytes)];
                    int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    hash.AppendData(buffer.AsSpan(0, read));
                }
                hash.AppendData(Separator); // record separator
            }

            string projectSkillsDir = Path.Combine(workingDirectory, ProjectSkillsDir);
            if (Directory.Exists(projectSkillsDir))
                AppendLong(hash, new DirectoryInfo(projectSkillsDir).LastWriteTimeUtc.Ticks, scratch);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                string globalSkillsDir = Path.Combine(userProfile, ProjectSkillsDir);
                if (Directory.Exists(globalSkillsDir))
                    AppendLong(hash, new DirectoryInfo(globalSkillsDir).LastWriteTimeUtc.Ticks, scratch);
            }
        }
        catch (Exception ex)
        {
            // Best-effort version: a partial fingerprint can match a stale
            // snapshot and serve an outdated prompt. Log the cause instead of
            // failing silently.
            if (_logger is not null)
                WorkspaceContextServiceLog.VersionFingerprintFailed(_logger, ex);
        }

        return BitConverter.ToInt64(hash.GetHashAndReset(), 0);
    }

    private static void AppendLong(IncrementalHash hash, long value, Span<byte> scratch)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(scratch, value);
        hash.AppendData(scratch);
    }

    private static void AppendChars(IncrementalHash hash, ReadOnlySpan<char> chars)
    {
        Span<byte> scratch = stackalloc byte[128];
        while (chars.Length > 0)
        {
            int take = Math.Min(chars.Length, 32);
            int written = Encoding.UTF8.GetBytes(chars[..take], scratch);
            hash.AppendData(scratch[..written]);
            chars = chars[take..];
        }
    }

    /// <summary>Evict one directory from this instance's workspace cache (tests/benchmarks).</summary>
    public void Invalidate(string workingDirectory) => _cache.TryRemove(workingDirectory, out _);

    /// <summary>
    ///     Load project context files found in <paramref name="workingDirectory" />,
    ///     enforcing one byte budget across ALL of them (#2230). Files are added
    ///     in probed order; the file that would exceed the remaining budget is cut
    ///     to that remainder and every later file is skipped. Both decisions emit
    ///     a warning so the cut is visible in the log.
    /// </summary>
    public IReadOnlyList<ContextFile> LoadContextFiles(string workingDirectory)
    {
        var files = new List<ContextFile>(ContextFileNames.Length);
        int remaining = MaxContextBytes;
        foreach (string name in ContextFileNames)
        {
            string path = Path.Combine(workingDirectory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            if (remaining <= 0)
            {
                _logger?.LogWarning(
                    "Project context budget: skipped '{File}' because the combined {Budget}-byte budget is already exhausted. Trim AGENTS.md/CLAUDE.md or move content into a skill.",
                    name, MaxContextBytes);
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0)
                {
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > remaining)
                {
                    _logger?.LogWarning(
                        "Project context budget: truncated '{File}' to {Kept} of {Total} bytes under the combined {Budget}-byte budget. Trim AGENTS.md/CLAUDE.md or move content into a skill.",
                        name, remaining, bytes.Length, MaxContextBytes);
                    bytes = bytes[..remaining];
                    remaining = 0;
                }
                else
                {
                    remaining -= bytes.Length;
                }

                files.Add(new ContextFile(name, Decode(bytes)));
            }
            catch (IOException ex)
            {
                // Unreadable context file — skip it; the prompt builds without it.
                if (_logger is not null)
                    WorkspaceContextServiceLog.ContextFileSkipped(_logger, ex, name);
            }
        }

        return files;
    }

    /// <summary>Decode context bytes as UTF-8, dropping a leading BOM like <c>File.ReadAllText</c>.</summary>
    private static string Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith(Utf8Bom))
        {
            span = span[Utf8Bom.Length..];
        }
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    ///     Render aggregated MCP server instructions as a prompt block
    ///     (ROP-D Z3). Returns <c>null</c> when no server reported instructions
    ///     so the builder skips the section entirely.
    /// </summary>
    public string? FormatMcpInstructions(IReadOnlyList<McpServerInstructions>? servers)
    {
        if (servers is null || servers.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder("Instructions from connected MCP servers:");
        foreach (McpServerInstructions server in servers)
        {
            sb.Append("\n- ").Append(server.ServerName).Append(": ")
              .Append(server.Instructions.ReplaceLineEndings(" "));
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Load available skills from the project-local and global skill
    ///     directories. Project-local entries win on name collisions.
    /// </summary>
    public IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? globalSkillsDir = string.IsNullOrEmpty(userProfile)
            ? null
            : Path.Combine(userProfile, ProjectSkillsDir);
        return LoadSkills(workingDirectory, globalSkillsDir);
    }

    /// <summary>
    ///     Testable core: the global skill directory is injected instead of
    ///     resolved from the user profile.
    /// </summary>
    public IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory, string? globalSkillsDir)
    {
        string projectSkillsDir = Path.Combine(workingDirectory, ProjectSkillsDir);
        return LoadSkillsFromRoots(projectSkillsDir, globalSkillsDir);
    }

    /// <summary>
    ///     Load skills from explicit skills roots (e.g. CLI <c>skill list</c>
    ///     with injected roots). Project root wins on name collisions.
    /// </summary>
    /// <remarks>
    ///     #1240: the returned array is sorted by name (ordinal) — a frozen
    ///     snapshot the per-turn consumer (<c>SystemPromptBuilder</c>) iterates
    ///     as-is instead of paying an <c>OrderBy</c> per turn. Discovery walks
    ///     two roots (project, then global) and two shapes per root (flat, then
    ///     directory), so per-root ordering alone cannot yield a globally
    ///     sorted list; the sort here is what makes it total. A front-matter
    ///     <c>name:</c> can also reorder within one root for the same reason.
    /// </remarks>
    public IReadOnlyList<SkillDescriptor> LoadSkillsFromRoots(string? projectSkillsDir, string? globalSkillsDir)
    {
        var skills = new List<SkillDescriptor>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        // Project first so it shadows same-named global skills.
        CollectSkills(projectSkillsDir, skills, seenNames);
        CollectSkills(globalSkillsDir, skills, seenNames);
        skills.Sort(CompareSkillsByName);
        return skills.ToArray();
    }

    private static int CompareSkillsByName(SkillDescriptor left, SkillDescriptor right) =>
        string.CompareOrdinal(left.Name, right.Name);

    private void CollectSkills(string? directory, List<SkillDescriptor> skills, HashSet<string> seenNames)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string[] skillFiles;
        try
        {
            skillFiles = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (IOException ex)
        {
            if (_logger is not null)
                WorkspaceContextServiceLog.SkillRootUnreadable(_logger, ex, directory);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger is not null)
                WorkspaceContextServiceLog.SkillRootUnreadable(_logger, ex, directory);
            return;
        }

        Array.Sort(skillFiles, StringComparer.Ordinal);

        foreach (string filePath in skillFiles)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            if (name.Length == 0 || !seenNames.Add(name))
            {
                continue;
            }

            skills.Add(new SkillDescriptor(name, ExtractDescription(filePath), filePath));
        }

        CollectSkillDirectories(directory, skills, seenNames);
    }

    /// <summary>
    ///     Second discovery shape: <c>&lt;root&gt;/&lt;name&gt;/SKILL.md</c>.
    ///     The skill name is the front-matter <c>name:</c> when present, else
    ///     the directory name. Runs after the flat scan in the same root so a
    ///     flat <c>&lt;name&gt;.md</c> wins ties within one root (documented
    ///     precedence); project-vs-global shadowing is unchanged.
    /// </summary>
    private void CollectSkillDirectories(string? directory, List<SkillDescriptor> skills, HashSet<string> seenNames)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(directory);
        }
        catch (IOException ex)
        {
            if (_logger is not null)
                WorkspaceContextServiceLog.SkillRootUnreadable(_logger, ex, directory);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger is not null)
                WorkspaceContextServiceLog.SkillRootUnreadable(_logger, ex, directory);
            return;
        }

        Array.Sort(subdirs, StringComparer.Ordinal);

        foreach (string subdir in subdirs)
        {
            string skillFile = Path.Combine(subdir, SkillFileName);
            if (!File.Exists(skillFile))
            {
                continue;
            }

            string name = ExtractName(skillFile) ?? Path.GetFileName(subdir);
            if (string.IsNullOrEmpty(name) || !seenNames.Add(name))
            {
                continue;
            }

            skills.Add(new SkillDescriptor(name, ExtractDescription(skillFile), skillFile));
        }
    }

    /// <summary>Front-matter <c>name:</c> from the first 20 lines, else null.</summary>
    private string? ExtractName(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            bool inFrontMatter = false;
            int linesRead = 0;
            while (linesRead++ < 20 && reader.ReadLine() is { } line)
            {
                if (linesRead == 1 && line.TrimEnd() == "---")
                {
                    inFrontMatter = true;
                    continue;
                }

                if (!inFrontMatter)
                {
                    return null;
                }

                if (line.TrimEnd() == "---")
                {
                    return null;
                }

                // Issue #1580: the trim → case-insensitive prefix → trim
                // → empty→null chain lives in FrontMatter.
                if (FrontMatter.GetFrontMatterField(line, "name") is { } name)
                {
                    return name;
                }
            }
        }
        catch (IOException ex)
        {
            // Fall through to null below (no front-matter name).
            if (_logger is not null)
                WorkspaceContextServiceLog.SkillFrontMatterUnreadable(_logger, ex, filePath);
        }

        return null;
    }

    /// <summary>
    ///     Read the skill's short description: front-matter
    ///     <c>description:</c> when present, else the first non-empty line
    ///     stripped of Markdown heading/list markers, capped to keep prompts lean.
    /// </summary>
    private string ExtractDescription(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            bool inFrontMatter = false;
            int linesRead = 0;
            while (linesRead++ < 20 && reader.ReadLine() is { } line)
            {
                if (linesRead == 1 && line.TrimEnd() == "---")
                {
                    inFrontMatter = true;
                    continue;
                }

                if (inFrontMatter)
                {
                    if (line.TrimEnd() == "---")
                    {
                        inFrontMatter = false;
                        continue;
                    }

                    if (FrontMatter.GetFrontMatterField(line, "description") is { } description)
                    {
                        // Cap stays here: the helper returns the raw field, the
                        // prompt-lean cap is a description-call-site policy.
                        return Cap(description);
                    }

                    continue;
                }

                string prose = line.TrimStart();
                if (prose.Length == 0)
                {
                    continue;
                }

                prose = prose.TrimStart('#', ' ', '-', '*').Trim();
                if (prose.Length > 0)
                {
                    return Cap(prose);
                }
            }
        }
        catch (IOException ex)
        {
            // Fall through to the generic descriptor below.
            if (_logger is not null)
                WorkspaceContextServiceLog.SkillFrontMatterUnreadable(_logger, ex, filePath);
        }

        return "No description provided.";
    }

    private static string Cap(string description) =>
        description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength];
}

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="WorkspaceContextService" />.
///     Each line closes an existence-tolerant fallback that used to swallow the
///     reason: a partial version fingerprint, a skipped context file, an
///     unreadable skill root, or a skill whose front-matter could not be read.
/// </summary>
internal static partial class WorkspaceContextServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Workspace context version fingerprint failed; the snapshot version is derived from partial input")]
    public static partial void VersionFingerprintFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Unreadable project context file '{File}' skipped while building the prompt")]
    public static partial void ContextFileSkipped(ILogger logger, Exception ex, string file);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Cannot enumerate skills in {Directory}; no skills contributed from this root")]
    public static partial void SkillRootUnreadable(ILogger logger, Exception ex, string directory);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Cannot read front-matter from skill file {File}; falling back to the directory name and generic description")]
    public static partial void SkillFrontMatterUnreadable(ILogger logger, Exception ex, string file);
}
