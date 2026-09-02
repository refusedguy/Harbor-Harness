using System.Text;
using System.Text.Json;
using System.Xml.Linq;
namespace Harbor.Build.Meta;
/// <summary>One project known to the mapper (name + csproj path, absolute or root-relative).</summary>
public sealed record ImpactProject(string Name, string CsprojPath);
/// <summary>Everything the mapper needs; decoupled from NUKE so it is testable offline.</summary>
public sealed record ImpactInput(string RootDirectory, IReadOnlyList<ImpactProject> Projects);
/// <summary>Options for the what-command.</summary>
public sealed record ImpactOptions(bool Strict = false, bool IncludePublish = false);
/// <summary>An affected project and why it is affected.</summary>
public sealed record AffectedProject(string Name, string Reason);
/// <summary>A suggested command with its justification.</summary>
public sealed record PlannedCommand(IReadOnlyList<string> Argv, string Why);
/// <summary>A deliberately omitted command and why.</summary>
public sealed record SkippedCommand(string Command, string Why);
/// <summary>The what-command answer: projects, plan, notes, confidence.</summary>
public sealed record ImpactAnswer(
    string Input,
    IReadOnlyList<AffectedProject> AffectedProjects,
    IReadOnlyList<PlannedCommand> Commands,
    IReadOnlyList<SkippedCommand> Skipped,
    IReadOnlyList<string> Notes,
    string Confidence)
{
    public const string High = "high";
    public const string Low = "low";
}
/// <summary>
///     Deterministic offline mapping "changed path → affected projects →
///     minimal build plan" for <c>./build.sh what</c>. Reads files only,
///     never executes anything. Project references are parsed from csproj
///     XML (<c>&lt;ProjectReference Include="…"/&gt;</c>, namespace-agnostic);
///     conditional or wildcarded references are still counted but lower the
///     confidence, per the conservative-dependency rule.
/// </summary>
public static class ImpactMapper
{
    private static readonly string[] GlobalTriggers =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "BannedApi.txt"
    ];
    /// <summary>Runs the analysis for a raw (absolute or relative) path.</summary>
    public static ImpactAnswer Analyze(ImpactInput input, string rawPath, ImpactOptions options)
    {
        var normalized = Normalize(input.RootDirectory, rawPath, out var insideRoot);
        if (!insideRoot)
        {
            return new ImpactAnswer(
                rawPath, [], [], [],
                ["path is outside the repository root"],
                ImpactAnswer.Low);
        }
        if (GlobalTriggers.Contains(normalized))
        {
            return FullCycleAnswer(rawPath, $"{normalized} affects every project in the solution");
        }
        if (normalized.StartsWith("build/", StringComparison.Ordinal))
        {
            return new ImpactAnswer(
                rawPath, [], [],
                [new SkippedCommand($"{BootstrapName()} <target>", "the build tool rebuilds itself on the next run; just rerun your target")],
                ["build/ changed — the bootstrap recompiles the build tool automatically"],
                ImpactAnswer.High);
        }
        if (normalized.StartsWith("docs/", StringComparison.Ordinal) ||
            normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return new ImpactAnswer(
                rawPath, [], [], [],
                ["documentation-only change — no build required"],
                ImpactAnswer.High);
        }
        if (normalized.StartsWith("providers/", StringComparison.Ordinal) &&
            normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return new ImpactAnswer(
                rawPath, [], [], [],
                ["provider JSON configs are read at runtime — no rebuild required"],
                ImpactAnswer.High);
        }
        if (normalized.StartsWith("samples/plugins", StringComparison.Ordinal))
        {
            return new ImpactAnswer(
                rawPath, [], [], [],
                ["sample plugins: CS-source plugins compile at runtime; DLL plugins build together with the solution"],
                ImpactAnswer.High);
        }
        var graph = ProjectGraph.Load(input);
        var owner = graph.FindOwningProject(normalized);
        if (owner is null)
        {
            return new ImpactAnswer(
                rawPath, [], [], [],
                ["path is not inside any known project (src/, apps/, tests/)"],
                ImpactAnswer.Low);
        }
        var (affected, closureUsedConditionalEdges) = graph.ReverseClosure(owner);
        var answer = BuildPlan(rawPath, affected, normalized, options);
        if (graph.HasParseFailures || closureUsedConditionalEdges)
        {
            answer = answer with
            {
                Confidence = ImpactAnswer.Low,
                Notes = [.. answer.Notes,
                    "csproj graph contains conditional/wildcard/unparseable references — treat the plan as advisory"]
            };
        }
        return answer;
    }
    /// <summary>Serializes the answer as one JSON document line.</summary>
    public static string ToJson(ImpactAnswer answer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("command", "what");
            writer.WriteString("input", answer.Input);
            WriteProjects(writer, answer.AffectedProjects);
            WritePlan(writer, answer.Commands, answer.Skipped);
            writer.WriteStartArray("notes");
            foreach (var note in answer.Notes)
            {
                writer.WriteStringValue(note);
            }
            writer.WriteEndArray();
            writer.WriteString("confidence", answer.Confidence);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    /// <summary>Renders the answer as a short human checklist.</summary>
    public static void RenderPretty(ImpactAnswer answer, BuildOutput output)
    {
        output.Human($"what: {answer.Input} (confidence: {answer.Confidence})");
        foreach (var project in answer.AffectedProjects)
        {
            output.Human($"  affected: {project.Name} ({project.Reason})");
        }
        foreach (var note in answer.Notes)
        {
            output.Human($"  note: {note}");
        }
        foreach (var command in answer.Commands)
        {
            output.Human($"  run: {string.Join(' ', command.Argv)}  # {command.Why}");
        }
        foreach (var skipped in answer.Skipped)
        {
            output.Human($"  skip: {skipped.Command}  # {skipped.Why}");
        }
    }
    private static void WriteProjects(Utf8JsonWriter writer, IReadOnlyList<AffectedProject> projects)
    {
        writer.WriteStartArray("affectedProjects");
        foreach (var project in projects)
        {
            writer.WriteStartObject();
            writer.WriteString("name", project.Name);
            writer.WriteString("reason", project.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
    private static void WritePlan(
        Utf8JsonWriter writer,
        IReadOnlyList<PlannedCommand> commands,
        IReadOnlyList<SkippedCommand> skipped)
    {
        writer.WriteStartObject("plan");
        writer.WriteStartArray("commands");
        foreach (var command in commands)
        {
            writer.WriteStartObject();
            writer.WriteStartArray("argv");
            foreach (var arg in command.Argv)
            {
                writer.WriteStringValue(arg);
            }
            writer.WriteEndArray();
            writer.WriteString("why", command.Why);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("skipped");
        foreach (var item in skipped)
        {
            writer.WriteStartObject();
            writer.WriteString("command", item.Command);
            writer.WriteString("why", item.Why);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    /// <summary>
    ///     Normalizes a raw path to a slash-separated path relative to the
    ///     repository root (per design §2.3: absolute or relative-to-root
    ///    inputs both land on the same canonical form).
    /// </summary>
    internal static string Normalize(string rootDirectory, string rawPath, out bool insideRoot)
    {
        insideRoot = true;
        var full = System.IO.Path.IsPathRooted(rawPath)
            ? System.IO.Path.GetFullPath(rawPath)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(rootDirectory, rawPath));
        var fullRoot = System.IO.Path.GetFullPath(rootDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot, comparison))
        {
            insideRoot = false;
            return string.Empty;
        }
        return full[fullRoot.Length..].TrimStart('/', '\\').Replace('\\', '/');
    }
    private static ImpactAnswer FullCycleAnswer(string rawPath, string why)
    {
        return new ImpactAnswer(
            rawPath, [],
            [
                new PlannedCommand(Command("Compile"), "full rebuild after solution-wide change"),
                new PlannedCommand(Command("Test"), "full test cycle after solution-wide change"),
                new PlannedCommand(Command("ArchitectureTests"), "layer rules may be affected solution-wide")
            ],
            [],
            [why],
            ImpactAnswer.High);
    }
    private static ImpactAnswer BuildPlan(
        string rawPath,
        List<(GraphProject Project, int Depth)> affected,
        string normalized,
        ImpactOptions options)
    {
        var affectedNames = affected.Select(a => a.Project.Name).ToHashSet(StringComparer.Ordinal);
        var touchesSrc = affected.Any(a => a.Project.RelativeDir.StartsWith("src/", StringComparison.Ordinal));
        var touchesTests = affected.Any(a => a.Project.RelativeDir.StartsWith("tests/", StringComparison.Ordinal));
        var touchesCli = affectedNames.Contains("Harbor.App.Cli");
        var touchesAvalonia = affectedNames.Contains("Harbor.App.Avalonia");
        var touchesIpc = affectedNames.Any(n => n.StartsWith("Harbor.Ipc.", StringComparison.Ordinal));
        var touchesArchitectureTests = affectedNames.Contains("Harbor.Architecture.Tests");
        var commands = new List<PlannedCommand>();
        var skipped = new List<SkippedCommand>();
        var notes = new List<string>();
        if (touchesSrc || touchesCli || touchesAvalonia || touchesIpc)
        {
            commands.Add(new PlannedCommand(Command("Compile"), "recompile after src/app changes"));
        }
        if (touchesArchitectureTests)
        {
            commands.Add(new PlannedCommand(Command("ArchitectureTests"),
                "architecture rules changed directly"));
        }
        else if (touchesSrc)
        {
            commands.Add(new PlannedCommand(Command("ArchitectureTests"),
                "src change can violate layer rules checked by Harbor.Architecture.Tests"));
        }
        if (touchesTests || touchesCli || touchesAvalonia)
        {
            commands.Add(new PlannedCommand(Command("Test"),
                "affected unit tests must pass after the change"));
        }
        if (touchesIpc)
        {
            commands.Add(new PlannedCommand(Command("PublishIpcServer"),
                "IPC transport changed — republish the ipc-server host"));
            commands.Add(new PlannedCommand(Command("PublishIpcClient"),
                "IPC transport changed — republish the ipc-client"));
        }
        if (touchesCli)
        {
            if (options.IncludePublish)
            {
                commands.Add(new PlannedCommand(Command("Publish"),
                    $"publish the CLI (default variant FrameworkDependent; current path: {normalized})"));
            }
            else
            {
                skipped.Add(new SkippedCommand(
                    $"{BootstrapName()} Publish",
                    "no publish requested — pass --include-publish to add it"));
            }
        }
        if (commands.Count == 0)
        {
            notes.Add("no rebuild commands derived — review manually");
        }
        var projects = affected
            .OrderBy(a => a.Depth)
            .ThenBy(a => a.Project.Name, StringComparer.Ordinal)
            .Select(a => new AffectedProject(
                a.Project.Name,
                a.Depth == 0 ? "path-inside-project-dir" : $"project-reference(depth={a.Depth})"))
            .ToList();
        return new ImpactAnswer(rawPath, projects, commands, skipped, notes, ImpactAnswer.High);
    }
    private static string[] Command(string target) => [BootstrapName(), target];
    private static string BootstrapName() => OperatingSystem.IsWindows() ? "build.ps1" : "./build.sh";
    /// <summary>csproj reference graph built by parsing ProjectReference items from every known project.</summary>
    private sealed class ProjectGraph
    {
        private readonly Dictionary<string, GraphProject> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ReferrerEdge>> _referrers = new(StringComparer.Ordinal);
        private ProjectGraph(ImpactInput input)
        {
            foreach (var project in input.Projects)
            {
                var absolute = System.IO.Path.IsPathRooted(project.CsprojPath)
                    ? System.IO.Path.GetFullPath(project.CsprojPath)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(input.RootDirectory, project.CsprojPath));
                _byPath[absolute] = new GraphProject(project.Name, absolute, ToRelative(input.RootDirectory, absolute));
            }
        }
        private sealed record ReferrerEdge(GraphProject Project, bool Conditional);
        /// <summary>True when any csproj could not be parsed (graph may be incomplete).</summary>
        public bool HasParseFailures { get; private set; }
        public static ProjectGraph Load(ImpactInput input)
        {
            var graph = new ProjectGraph(input);
            foreach (var project in graph._byPath.Values)
            {
                foreach (var (referencedPath, conditional) in graph.ParseReferences(project.CsprojPath))
                {
                    if (!graph._referrers.TryGetValue(referencedPath, out var list))
                    {
                        list = new List<ReferrerEdge>();
                        graph._referrers[referencedPath] = list;
                    }
                    list.Add(new ReferrerEdge(project, conditional));
                }
            }
            return graph;
        }
        public GraphProject? FindOwningProject(string normalizedRelativePath)
        {
            GraphProject? best = null;
            var bestLength = -1;
            foreach (var project in _byPath.Values)
            {
                var dir = project.RelativeDir;
                if (dir.Length == 0 ||
                    bestLength >= dir.Length ||
                    !normalizedRelativePath.StartsWith(dir, StringComparison.Ordinal) ||
                    (normalizedRelativePath.Length > dir.Length &&
                     normalizedRelativePath[dir.Length] != '/'))
                {
                    continue;
                }
                best = project;
                bestLength = dir.Length;
            }
            return best;
        }
        public (List<(GraphProject Project, int Depth)> Closure, bool UsedConditionalEdges) ReverseClosure(GraphProject owner)
        {
            var closure = new List<(GraphProject, int)> { (owner, 0) };
            var seen = new HashSet<string>(StringComparer.Ordinal) { owner.Name };
            var frontier = new List<(GraphProject Project, bool Conditional)> { (owner, false) };
            var depth = 0;
            var usedConditional = false;
            while (frontier.Count > 0)
            {
                depth++;
                var next = new List<(GraphProject, bool)>();
                foreach (var (project, _) in frontier)
                {
                    if (!_referrers.TryGetValue(project.CsprojPath, out var edges))
                    {
                        continue;
                    }
                    foreach (var edge in edges)
                    {
                        usedConditional |= edge.Conditional;
                        if (seen.Add(edge.Project.Name))
                        {
                            next.Add((edge.Project, edge.Conditional));
                            closure.Add((edge.Project, depth));
                        }
                    }
                }
                frontier = next;
            }
            return (closure, usedConditional);
        }
        private List<(string Path, bool Conditional)> ParseReferences(string csprojPath)
        {
            var references = new List<(string, bool)>();
            var directory = System.IO.Path.GetDirectoryName(csprojPath);
            if (directory is null)
            {
                // csproj directly at a filesystem root — nothing sane to resolve.
                HasParseFailures = true;
                return references;
            }
            XDocument document;
            try
            {
                document = XDocument.Load(csprojPath, LoadOptions.None);
            }
            catch (Exception ex) when (
                ex is IOException or System.Xml.XmlException or UnauthorizedAccessException or ArgumentException)
            {
                HasParseFailures = true;
                return references;
            }
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }
                // Conservative rule: keep the dependency but distrust the plan
                // when the reference is conditional or computed.
                var conditional = element.Attribute("Condition") is not null ||
                                  element.HasElements ||
                                  include.Contains('$') ||
                                  include.Contains('*');
                references.Add((System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(directory, include)), conditional));
            }
            return references;
        }
        private static string ToRelative(string root, string absolutePath)
        {
            var fullRoot = System.IO.Path.GetFullPath(root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return absolutePath.StartsWith(fullRoot, comparison)
                ? absolutePath[fullRoot.Length..].TrimStart('/', '\\').Replace('\\', '/')
                : absolutePath.Replace('\\', '/');
        }
    }
    private sealed record GraphProject(string Name, string CsprojPath, string RelativeDir);
}
