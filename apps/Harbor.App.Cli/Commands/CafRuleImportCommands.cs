// #3409: the real consumer of RuleImporter. `harbor import-rules` reads legacy
// editor rule files, converts them through the existing pure RuleImporter and
// appends the marked appendix to AGENTS.md. This is only the I/O shell around
// the leaf: all conversion, bounds checking and deterministic markdown come
// from RuleImporter itself, so there is no second copy of that logic here.
// BCL file I/O only, no reflection, no dynamic code, AOT-safe.

using CSharpFunctionalExtensions;
using ConsoleAppFramework;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Commands;

internal static class CafRuleImportCommands
{
    private const string StartMarker = "<!-- harbor:imported-rules:start -->";
    private const string EndMarker = "<!-- harbor:imported-rules:end -->";

    /// <summary>Import legacy editor rule files into a project's AGENTS.md.</summary>
    /// <param name="project">Directory whose AGENTS.md receives the rules (default: current directory).</param>
    /// <param name="files">Legacy rule files to import (.cursorrules, .roorules, .kilocoderules).</param>
    public static async Task<int> ImportRules(
        string? project = null,
        [Argument]params string[] files)
    {
        string projectDir = string.IsNullOrWhiteSpace(project)
            ? Directory.GetCurrentDirectory()
            : project!;

        if (files.Length == 0)
        {
            Console.Error.WriteLine("import-rules: no legacy rule files were given.");
            return 1;
        }

        var legacy = new List<LegacyRuleFile>(files.Length);
        foreach (string file in files)
        {
            string path = Path.IsPathRooted(file) ? file : Path.Combine(projectDir, file);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"import-rules: file not found: {path}");
                return 1;
            }

            string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            legacy.Add(new LegacyRuleFile(path, content));
        }

        Result<IReadOnlyList<ImportedRuleSection>> converted = RuleImporter.ImportMany(legacy);
        if (converted.IsFailure)
        {
            Console.Error.WriteLine($"import-rules: {converted.Error}");
            return 1;
        }

        string appendix = RuleImporter.ToAgentsMarkdown(converted.Value);
        string agentsPath = Path.Combine(projectDir, "AGENTS.md");
        string existing = File.Exists(agentsPath)
            ? await File.ReadAllTextAsync(agentsPath).ConfigureAwait(false)
            : string.Empty;

        await File.WriteAllTextAsync(agentsPath, ReplacePreviousImport(existing) + appendix)
            .ConfigureAwait(false);

        Console.Out.WriteLine(
            $"Imported {converted.Value.Count} rule file(s) into {agentsPath}.");
        return 0;
    }

    // Re-imports replace the block left by an earlier run instead of stacking
    // duplicate copies, so running the verb twice converges on one appendix.
    private static string ReplacePreviousImport(string existing)
    {
        int start = existing.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return existing;
        }

        int end = existing.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return existing;
        }

        end += EndMarker.Length;
        // ToAgentsMarkdown prefixes the block with a newline; consume it too.
        if (start > 0 && existing[start - 1] == '\n')
        {
            start--;
        }

        return existing[..start] + existing[end..];
    }
}
