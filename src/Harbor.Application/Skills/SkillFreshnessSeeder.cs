using System.Security.Cryptography;
using System.Text;

namespace Harbor.Application.Skills;

// KILLER_FEATURES §2.7 Feature 10 (Orca `SkillFreshnessStatusPill.tsx`),
// issue #23 slice 2: host seeding for the slice-1 model. The seeder compares
// installed skill content (sha256 over each `SKILL.md`) against the snapshot
// recorded in `skills-lock.json` (`computedHash`) and returns one snapshot per
// skill — installed-only (untracked), locked-only (missing), or both (current
// vs changed, derived downstream by `SkillFreshnessEntry.Status`).
//
// BCL-only on purpose: no Harbor references (not even same-assembly
// `WorkspaceContextSource`, whose discovery shapes are mirrored here instead)
// so the Application layer keeps its Domain-only edge and the
// `FullLayerMatrixTests` row for `Harbor.Application` stays green. The CLI
// composition root maps snapshots onto `SkillFreshnessEntry` and feeds the
// shared `SkillFreshnessModel`.
//
// Name/discovery contract (mirrors `WorkspaceContextSource`):
// `<root>/<name>/SKILL.md` (front-matter `name:` wins, else the directory
// name) plus legacy flat `<root>/<name>.md` (file name wins ties within one
// root); project root shadows the global root. Output is ordinal-sorted by
// name so every host paints rows deterministically.

/// <summary>
///     Seeder output for one skill (KILLER_FEATURES §2.7 Feature 10):
///     the directory/file name plus the installed content hash (null when not
///     installed) and the lockfile hash (null when never locked). The CLI maps
///     this onto <c>SkillFreshnessEntry</c>, which derives the pill status.
/// </summary>
/// <param name="Name">Skill name (directory name, front-matter override, or flat-file stem).</param>
/// <param name="InstalledHash">Lowercase sha256 hex of the installed <c>SKILL.md</c> bytes, or null when not installed (or unreadable).</param>
/// <param name="LockedHash">Hash recorded in <c>skills-lock.json</c> (<c>computedHash</c>), or null when never locked.</param>
public sealed record SkillFreshnessSnapshot(string Name, string? InstalledHash, string? LockedHash);

/// <summary>
///     Builds <see cref="SkillFreshnessSnapshot" /> rows from the workspace
///     (KILLER_FEATURES §2.7 Feature 10, issue #23 slice 2). Existence-tolerant
///     like <c>WorkspaceContextSource</c>: missing roots, a missing lockfile,
///     or a malformed lockfile yield untracked/missing rows, never a throw —
///     a workspace without skills (or without a lockfile) is normal.
/// </summary>
public static class SkillFreshnessSeeder
{
    /// <summary>Skill descriptor file inside a skill directory.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>Front-matter scan budget, mirroring <c>WorkspaceContextSource</c>.</summary>
    private const int FrontMatterScanLines = 20;

    /// <summary>
    ///     Seed freshness snapshots from two skill roots plus a lockfile.
    ///     Any argument may be null (skipped); results are ordinal-sorted.
    /// </summary>
    /// <param name="projectSkillsDir">Project <c>.harbor/skills</c> root (wins on collisions).</param>
    /// <param name="globalSkillsDir">Global <c>~/.harbor/skills</c> root (fallback).</param>
    /// <param name="lockFilePath">Path to <c>skills-lock.json</c> (missing/malformed ⇒ empty snapshot map).</param>
    public static IReadOnlyList<SkillFreshnessSnapshot> Seed(
        string? projectSkillsDir,
        string? globalSkillsDir,
        string? lockFilePath)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectSkillPaths(projectSkillsDir, paths);
        CollectSkillPaths(globalSkillsDir, paths);

        Dictionary<string, string?> locked = ReadLockedHashes(lockFilePath);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in paths.Keys)
        {
            _ = names.Add(name);
        }

        foreach (string name in locked.Keys)
        {
            _ = names.Add(name);
        }

        var result = new List<SkillFreshnessSnapshot>(names.Count);
        foreach (string name in names)
        {
            string? installedHash = null;
            if (paths.TryGetValue(name, out string? path))
            {
                installedHash = HashFile(path);
            }

            _ = locked.TryGetValue(name, out string? lockedHash);
            result.Add(new SkillFreshnessSnapshot(name, installedHash, lockedHash));
        }

        return result;
    }

    /// <summary>
    ///     Collect <c>name → SKILL.md path</c> from one root: flat
    ///     <c>*.md</c> first (wins ties within the root), then
    ///     <c>&lt;name&gt;/SKILL.md</c> directories. First root wins across
    ///     roots (project shadows global) via <see cref="Dictionary{TKey,TValue}.TryAdd" />.
    /// </summary>
    private static void CollectSkillPaths(string? directory, Dictionary<string, string> paths)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string[] flatFiles;
        try
        {
            flatFiles = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(flatFiles, StringComparer.Ordinal);
        for (int i = 0; i < flatFiles.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(flatFiles[i]);
            if (name.Length != 0)
            {
                _ = paths.TryAdd(name, flatFiles[i]);
            }
        }

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(directory);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(subdirs, StringComparer.Ordinal);
        for (int i = 0; i < subdirs.Length; i++)
        {
            string skillFile = Path.Combine(subdirs[i], SkillFileName);
            if (!File.Exists(skillFile))
            {
                continue;
            }

            string name = ReadFrontMatterName(skillFile) ?? Path.GetFileName(subdirs[i]);
            if (!string.IsNullOrEmpty(name))
            {
                _ = paths.TryAdd(name, skillFile);
            }
        }
    }

    /// <summary>Front-matter <c>name:</c> from the first lines, else null (mirrors <c>WorkspaceContextSource</c>).</summary>
    private static string? ReadFrontMatterName(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8);
            bool inFrontMatter = false;
            for (int line = 1; line <= FrontMatterScanLines; line++)
            {
                string? text = reader.ReadLine();
                if (text is null)
                {
                    return null;
                }

                if (line == 1 && text.TrimEnd() == "---")
                {
                    inFrontMatter = true;
                    continue;
                }

                if (!inFrontMatter)
                {
                    return null;
                }

                if (text.TrimEnd() == "---")
                {
                    return null;
                }

                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    string name = trimmed["name:".Length..].Trim();
                    return name.Length == 0 ? null : name;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable file — fall through to the directory-name fallback.
        }
        catch (UnauthorizedAccessException)
        {
            // Unreadable file — fall through to the directory-name fallback.
        }

        return null;
    }

    /// <summary>Lowercase sha256 hex of the file bytes, or null when unreadable (surfaces as a missing pill).</summary>
    private static string? HashFile(string filePath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Read <c>name → computedHash</c> from <c>skills-lock.json</c> with a
    ///     manual <see cref="Utf8JsonReader" /> walk (no DOM allocs on the
    ///     seeding path). Missing file ⇒ empty map; malformed JSON ⇒ empty
    ///     map (an untrusted lockfile must not flip pills halfway).
    ///     Shape: <c>{"version": 1, "skills": {"name": {"computedHash": "…"}}}.</c>
    /// </summary>
    private static Dictionary<string, string?> ReadLockedHashes(string? lockFilePath)
    {
        var locked = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(lockFilePath) || !File.Exists(lockFilePath))
        {
            return locked;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(lockFilePath);
        }
        catch (IOException)
        {
            return locked;
        }
        catch (UnauthorizedAccessException)
        {
            return locked;
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return locked;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }

                bool isSkills = reader.ValueTextEquals("skills"u8);
                if (!reader.Read())
                {
                    break;
                }

                if (!isSkills || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                ReadSkillsObject(ref reader, locked);
            }
        }
        catch (JsonException)
        {
            // Malformed lockfile — distrust the whole snapshot, not just the tail.
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        return locked;
    }

    /// <summary>Walk the <c>skills</c> object: each property is a skill name mapping to an entry object.</summary>
    private static void ReadSkillsObject(ref Utf8JsonReader reader, Dictionary<string, string?> locked)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                return;
            }

            if (name.Length == 0 || reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            locked[name] = ReadComputedHash(ref reader);
        }
    }

    /// <summary>Walk one skill entry object, returning its <c>computedHash</c> string (or null).</summary>
    private static string? ReadComputedHash(ref Utf8JsonReader reader)
    {
        string? computedHash = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return computedHash;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            bool isHash = reader.ValueTextEquals("computedHash"u8);
            if (!reader.Read())
            {
                return computedHash;
            }

            if (!isHash)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                computedHash = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return computedHash;
    }
}
