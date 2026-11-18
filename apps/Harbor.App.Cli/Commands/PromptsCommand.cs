using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor prompt list|show|send|install|uninstall</c> — manage and run
///     named prompts, the Harbor equivalent of Puck's named prompts
///     (<c>ship</c>, <c>update-base</c>, <c>review</c>, …) from issue #2676.
///     A prompt is a single Markdown file <c>&lt;name&gt;.md</c>. Global prompts
///     live in <c>~/.harbor/prompts</c>, project-local ones in
///     <c>&lt;cwd&gt;/.harbor/prompts</c>; project shadows global. The file may
///     start with a <c>---</c> frontmatter block carrying a
///     <c>description:</c> line — everything after the closing delimiter is
///     the prompt body sent to the agent.
/// </summary>
/// <remarks>
///     <c>send</c> substitutes <c>{{args}}</c> with the joined extra words and
///     <c>{{1}}</c>, <c>{{2}}</c>, … with the corresponding positional word,
///     then forwards the resolved text to <see cref="AskVerb" />. No new seam,
///     no reflection, no AOT-hostile constructs: this is a CLI-local command
///     following <see cref="SkillsCommand" />.
/// </remarks>
public sealed partial class PromptsCommand
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _globalRoot;
    private readonly string _projectRoot;
    private readonly Func<string, Task<int>> _sender;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public PromptsCommand(
        TextWriter output,
        TextWriter error,
        string? globalRoot = null,
        string? projectRoot = null,
        Func<string, Task<int>>? sender = null)
    {
        _output = output;
        _error = error;
        _globalRoot = globalRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "prompts");
        _projectRoot = projectRoot ?? Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "prompts");
        _sender = sender ?? (text => AskVerb.RunAsync(NullLogger.Instance, [text]));
    }

    public string Name => "prompt";

    public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        return sub switch
        {
            "list" => ListAsync(),
            "show" => ShowAsync(args.Skip(1).ToArray()),
            "send" => SendAsync(args.Skip(1).ToArray()),
            "install" => InstallAsync(args.Skip(1).ToArray()),
            "uninstall" or "remove" or "rm" => UninstallAsync(args.Skip(1).ToArray()),
            _ => PrintUsage()
        };
    }

    private async Task<int> ListAsync()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] { (_projectRoot, "project"), (_globalRoot, "global") };
        foreach ((string root, string scope) in roots)
        {
            if (!Directory.Exists(root))
                continue;

            var files = Directory.EnumerateFiles(root, "*.md")
                .OrderBy(static f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!PlainPromptName().IsMatch(name) || !seen.Add(name))
                    continue;

                var (description, _) = Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false));
                _output.WriteLine(description.Length > 0
                    ? $"{name} [{scope}] - {description}"
                    : $"{name} [{scope}]");
            }
        }

        if (seen.Count == 0)
            _output.WriteLine("(no prompts installed)");
        return 0;
    }

    private async Task<int> ShowAsync(string[] args)
    {
        if (args.Length == 0 || !PlainPromptName().IsMatch(args[0]))
            return Usage("Usage: harbor prompt show <name>");

        var doc = await ResolveAsync(args[0]).ConfigureAwait(false);
        if (doc is null)
            return NoSuchPrompt(args[0]);

        _output.WriteLine(doc.Body);
        return 0;
    }

    private async Task<int> SendAsync(string[] args)
    {
        if (args.Length == 0 || !PlainPromptName().IsMatch(args[0]))
            return Usage("Usage: harbor prompt send <name> [args...]");

        var doc = await ResolveAsync(args[0]).ConfigureAwait(false);
        if (doc is null)
            return NoSuchPrompt(args[0]);

        string[] extra = args.Skip(1).ToArray();
        string prompt = Substitute(doc.Body, extra);
        return await _sender(prompt).ConfigureAwait(false);
    }

    private static string Substitute(string body, string[] args)
    {
        string result = body.Replace("{{args}}", string.Join(' ', args), StringComparison.Ordinal);
        for (int i = 0; i < args.Length; i++)
            result = result.Replace("{{" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}", args[i], StringComparison.Ordinal);
        return result;
    }

    private async Task<int> InstallAsync(string[] args)
    {
        bool force = args.Contains("--force", StringComparer.Ordinal);
        bool toProject = args.Contains("--project", StringComparer.Ordinal);
        string? source = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (source is null)
            return Usage("Usage: harbor prompt install <prompt.md> [--project] [--force]");

        if (!".md".Equals(Path.GetExtension(source), StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
        {
            await _error.WriteLineAsync($"Not an installable prompt (.md file): '{source}'").ConfigureAwait(false);
            return 1;
        }

        string name = Path.GetFileNameWithoutExtension(source);
        if (!PlainPromptName().IsMatch(name))
        {
            await _error.WriteLineAsync($"Invalid prompt name '{name}'.").ConfigureAwait(false);
            return 1;
        }

        string root = Path.GetFullPath(toProject ? _projectRoot : _globalRoot);
        string target = Path.GetFullPath(Path.Combine(root, name + ".md"));
        if (!target.StartsWith(root, PathComparison))
        {
            await _error.WriteLineAsync("Resolved target escaped the prompts root — refusing.").ConfigureAwait(false);
            return 1;
        }

        if (File.Exists(target) && !force)
        {
            await _error.WriteLineAsync($"{target} already exists; pass --force to overwrite.").ConfigureAwait(false);
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, await File.ReadAllTextAsync(source).ConfigureAwait(false)).ConfigureAwait(false);
        await _output.WriteLineAsync($"Installed {(toProject ? "project" : "global")} prompt → {target}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> UninstallAsync(string[] args)
    {
        string? name = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (name is null || !PlainPromptName().IsMatch(name))
            return Usage("Usage: harbor prompt uninstall <name>");

        foreach (string root in new[] { _projectRoot, _globalRoot })
        {
            string rootFull = Path.GetFullPath(root);
            string candidate = Path.GetFullPath(Path.Combine(rootFull, name + ".md"));
            if (!candidate.StartsWith(rootFull, PathComparison) || !File.Exists(candidate))
                continue;

            File.Delete(candidate);
            await _output.WriteLineAsync($"Uninstalled {candidate}").ConfigureAwait(false);
            return 0;
        }

        await _error.WriteLineAsync($"No prompt named '{name}' found in {_projectRoot} or {_globalRoot}.").ConfigureAwait(false);
        return 1;
    }

    private async Task<PromptDoc?> ResolveAsync(string name)
    {
        foreach (string root in new[] { _projectRoot, _globalRoot })
        {
            string path = Path.GetFullPath(Path.Combine(root, name + ".md"));
            if (!File.Exists(path))
                continue;

            var (_, body) = Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            return new PromptDoc(body);
        }
        return null;
    }

    private Task<int> NoSuchPrompt(string name)
    {
        _error.WriteLine($"No prompt named '{name}' found in {_projectRoot} or {_globalRoot}.");
        return Task.FromResult(1);
    }

    private Task<int> Usage(string text)
    {
        _error.WriteLine(text);
        return Task.FromResult(2);
    }

    private Task<int> PrintUsage()
    {
        _error.WriteLine("""
                         Usage: harbor prompt [list|show|send|install|uninstall]
                           prompt list                       show global + project prompts (project shadows global)
                           prompt show <name>                print the resolved prompt body
                           prompt send <name> [args...]      run a named prompt through the agent
                           prompt install <prompt.md>        copy into ~/.harbor/prompts (--project targets <cwd>/.harbor/prompts)
                           prompt uninstall <name>           remove from either scope
                         """);
        return Task.FromResult(2);
    }

    /// <summary>Split an optional <c>---</c> frontmatter block from the prompt body.</summary>
    private static (string Description, string Body) Parse(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal) &&
            !text.StartsWith("---\r\n", StringComparison.Ordinal))
            return (string.Empty, text.Trim());

        using var reader = new StringReader(text);
        _ = reader.ReadLine(); // opening delimiter
        string description = string.Empty;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == "---")
                break;
            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                description = line["description:".Length..].Trim();
        }
        return (description, reader.ReadToEnd().Trim());
    }

    private sealed record PromptDoc(string Body);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPromptName();
}
