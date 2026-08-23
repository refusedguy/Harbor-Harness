using System.Reflection;
using System.Text;
using System.Text.Json;
using Nuke.Common;
namespace Harbor.Build.Meta;
/// <summary>One documented parameter of a target (global NUKE [Parameter]).</summary>
public sealed record TargetParam(string Name, string Type, string? Default, string Description);
/// <summary>One documented output location of a target.</summary>
public sealed record TargetOutput(string Kind, string? Path);
/// <summary>
///     Hand-written documentation entry for one NUKE target. The set of
///     entries is reconciled 1:1 against reflection over the <c>Target</c>
///     properties of the build class — a mismatch fails loud with a diff
///     (see <see cref="Verify" />), so this catalog cannot silently drift
///     from the code the way stale header comments did.
/// </summary>
public sealed record TargetDoc(
    string Name,
    string Summary,
    string[] DependsOn,
    string[] Before,
    TargetParam[] Parameters,
    TargetOutput[] Outputs,
    string[] Examples,
    string[] AgentHints,
    bool IsDefault = false);
/// <summary>
///     Source of truth for <c>./build.sh list|help</c>: the catalog of all
///     targets, meta commands and global flags, kept honest by reflection.
/// </summary>
public static class TargetCatalog
{
    private static readonly TargetDoc[] Entries =
    [
        new("Clean", "Delete bin/obj under src/apps/tests/samples and clear artifacts/.",
            [], ["Restore"],
            [],
            [new TargetOutput("dir", "artifacts/ (cleared)")],
            ["./build.sh Clean"],
            ["safe to run repeatedly; runs automatically before Restore"]),
        new("Restore", "dotnet restore for the whole solution.",
            [], [],
            [],
            [new TargetOutput("none", null)],
            ["./build.sh Restore"],
            ["runs as a dependency of every build-ish target anyway"]),
        new("Compile", "dotnet build Harbor.slnx in Release (parallel).",
            ["Restore"], [],
            [new TargetParam("--configuration", "enum[Debug|Release]", "release", "build configuration")],
            [new TargetOutput("dir", "src/**/bin/Release")],
            ["./build.sh Compile", "./build.sh Compile --configuration Debug"],
            ["default target; tests need it first because they run --no-build"], IsDefault: true),
        new("Test", "dotnet test the whole solution --no-build after Compile.",
            ["Compile"], [],
            [new TargetParam("--configuration", "enum[Debug|Release]", "release", "must match the Compile configuration")],
            [new TargetOutput("none", null)],
            ["./build.sh Test", "./build.sh Test --configuration Debug"],
            ["does not compile; run ./build.sh Compile first or rely on DependsOn"]),
        new("ArchitectureTests",
            "Run only tests/Harbor.Architecture.Tests to validate layer dependencies.",
            ["Compile"], [],
            [],
            [new TargetOutput("none", null)],
            ["./build.sh ArchitectureTests"],
            ["fast CI signal after touching project references or layering"]),
        new("Publish", "Publish an app into artifacts/publish/<app>/<variant>.",
            ["Compile"], [],
            [
                new TargetParam("--app-name", "string", "Harbor.App.Cli", "app project to publish"),
                new TargetParam("--variant", "enum[FrameworkDependent|SelfContained|SingleFile|SingleFileSelfContained|Trimmed|AOT]", "FrameworkDependent", "publish variant"),
                new TargetParam("--runtime", "string", "linux-x64", "target runtime identifier"),
                new TargetParam("--minimal", "bool", "false", "disable plugins/scripting/SpectreTui (required for AOT/Trimmed)"),
                new TargetParam("--with-plugins", "bool", "true", "include plugin runtime"),
                new TargetParam("--with-scripting", "bool", "true", "include Jint scripting"),
                new TargetParam("--with-spectre-tui", "bool", "true", "include Spectre TUI renderer"),
                new TargetParam("--with-all-providers", "bool", "true", "all LLM providers instead of Ollama only"),
                new TargetParam("--with-all-tools", "bool", "true", "all builtin tools instead of core six")
            ],
            [new TargetOutput("dir", "artifacts/publish/<app>/<variant>")],
            ["./build.sh Publish --variant SingleFileSelfContained", "./build.sh Publish --variant AOT --minimal"],
            ["AOT/Trimmed require --minimal (validated before anything executes)"]),
        new("PublishArchive", "Publish an app and wrap the output into tar.gz or zip.",
            ["Compile"], [],
            [
                new TargetParam("--archive", "enum[None|TarGz|Zip]", "TarGz when archiving", "archive format"),
                new TargetParam("--variant", "enum[…]", "FrameworkDependent", "see Publish")
            ],
            [new TargetOutput("file", "artifacts/archives/<slug>.tar.gz|.zip")],
            ["./build.sh PublishArchive --variant SingleFileSelfContained --archive TarGz"],
            ["equivalent to Publish followed by Archive"]),
        new("Release",
            "Full release matrix: publish variants → archives → GitHub release upload.",
            ["Compile"], [],
            [
                new TargetParam("--release-tag", "string", "(required)", "git tag for the GitHub release"),
                new TargetParam("--release-repo", "string", "harbor-sh/harbor", "GitHub owner/name"),
                new TargetParam("--gh-token", "env GH_TOKEN", null, "upload is skipped with a warning when absent")
            ],
            [new TargetOutput("file", "artifacts/archives/*.tar.gz + GitHub assets")],
            ["./build.sh Release --release-tag v0.7.0"],
            ["requires clean git tree in practice; AOT variant added only when flags are AOT-compatible"]),
        new("PublishIpcServer",
            "Publish the CLI as ipc-server host (HarborMode=ipc-server) into artifacts/ipc-server.",
            ["Compile"], [],
            [],
            [new TargetOutput("dir", "artifacts/ipc-server")],
            ["./build.sh PublishIpcServer"],
            ["hosts AgentLoop behind MessagePack-over-pipe"]),
        new("PublishIpcClient",
            "Publish the thin ipc-client CLI (HarborMode=ipc-client) into artifacts/ipc-client.",
            ["Compile"], [],
            [],
            [new TargetOutput("dir", "artifacts/ipc-client")],
            ["./build.sh PublishIpcClient"],
            ["connects to a running ipc-server"]),
        new("List", "Print the machine-readable catalog of targets (this document).",
            [], [],
            [new TargetParam("--format", "enum[Pretty|Json]", "pretty", "json prints one JSON object")],
            [new TargetOutput("stdout", null)],
            ["./build.sh list --format json"],
            ["never invent build commands — ask here first"]),
        new("Help", "Human-readable alias for List (always pretty).",
            [], [], [], [new TargetOutput("stdout", null)], ["./build.sh Help"],
            ["same data as list, formatted for humans"]),
        new("Doctor", "Offline environment checks with stable ids and fix hints.",
            [], [],
            [new TargetParam("--check", "csv<string>", "(all)", "comma-separated check ids")],
            [new TargetOutput("stdout", null)],
            ["./build.sh doctor --format json", "./build.sh doctor --check dotnet.sdk,tar.available"],
            ["exit code 3 when any check fails; no network access"]),
        new("What", "Map a changed path to affected projects and the minimal command plan.",
            [], [],
            [
                new TargetParam("--path", "string", "(required)", "file or directory to analyze"),
                new TargetParam("--strict", "bool", "false", "exit 4 when confidence is low"),
                new TargetParam("--include-publish", "bool", "false", "add Publish commands for app changes")
            ],
            [new TargetOutput("stdout", null)],
            ["./build.sh what --path src/Harbor.Tools.Builtin/GlobTool.cs --format json"],
            ["reads files only, never builds; use --dry-run on suggested commands"])
    ];
    /// <summary>Meta commands advertised alongside targets.</summary>
    public static readonly (string Name, string Usage)[] MetaCommands =
    [
        ("list", "./build.sh list [--format pretty|json]"),
        ("help", "./build.sh help"),
        ("doctor", "./build.sh doctor [--format json] [--check id1,id2]"),
        ("what", "./build.sh what --path <path> [--strict] [--include-publish]")
    ];
    /// <summary>Global flags advertised alongside targets.</summary>
    public static readonly (string Name, string Values, string Description)[] GlobalFlags =
    [
        ("--format", "pretty|json", "output format (json = machine lines on stdout, noise on stderr)"),
        ("--dry-run", "", "show the plan (argv + planned artifacts) without executing"),
        ("--out", "", "duplicate the machine stream to a file")
    ];
    /// <summary>All documented target entries (source of truth for list/help).</summary>
    public static IReadOnlyCollection<TargetDoc> Docs => Entries;
    /// <summary>
    ///     Parses raw command-line arguments into candidate target names
    ///     (positional tokens plus <c>--target</c> values; parameter values
    ///     of other flags are excluded). Compared case-insensitively — NUKE
    ///     matches target names the same way. Used to distinguish invoked
    ///     targets from pulled-in dependencies for dry-run status reporting.
    /// </summary>
    public static HashSet<string> ParseInvokedTargetNames(IEnumerable<string> args)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previous = string.Empty;
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
            {
                previous = arg;
                continue;
            }
            if (previous.Equals("--target", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(arg);
            }
            else if (!previous.StartsWith('-'))
            {
                names.Add(arg);
            }
            previous = arg;
        }
        return names;
    }
    /// <summary>
    ///     Builds the list document (§2.1 of the design) and verifies the
    ///     hand-written catalog against reflection over the live build:
    ///     target names must match 1:1 and dependency edges
    ///     (<c>DependsOn</c> / <c>Before</c>) must match exactly. Any drift
    ///     throws with a diff so agents never read a stale catalog.
    /// </summary>
    public static string BuildListDocumentJson(NukeBuild build)
    {
        var reflected = ReflectExecutableTargets(build);
        Verify(Entries, reflected);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("command", "list");
            var defaultEntry = Entries.FirstOrDefault(e => e.IsDefault);
            writer.WriteString("defaultTarget", defaultEntry?.Name ?? string.Empty);
            writer.WriteStartArray("targets");
            foreach (var entry in Entries)
            {
                WriteTarget(writer, entry);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("metaCommands");
            foreach (var (name, usage) in MetaCommands)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("usage", usage);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("globalFlags");
            foreach (var (name, values, description) in GlobalFlags)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                if (values.Length > 0)
                {
                    writer.WriteStartArray("values");
                    foreach (var value in values.Split('|'))
                    {
                        writer.WriteStringValue(value);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteString("description", description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    /// <summary>Renders the catalog as human-readable text (used by help/List pretty mode).</summary>
    public static void RenderPretty(BuildOutput output)
    {
        output.Human("Targets (run via ./build.sh <Name>):");
        foreach (var entry in Entries)
        {
            var deps = entry.DependsOn.Length > 0 ? $" [dependsOn: {string.Join(", ", entry.DependsOn)}]" : string.Empty;
            var def = entry.IsDefault ? " (default)" : string.Empty;
            output.Human($"  {entry.Name}{def} — {entry.Summary}{deps}");
            foreach (var hint in entry.AgentHints)
            {
                output.Human($"      hint: {hint}");
            }
        }
        output.Human("Meta commands:");
        foreach (var (name, usage) in MetaCommands)
        {
            output.Human($"  {usage}");
        }
        output.Human("Global flags:");
        foreach (var (name, _, description) in GlobalFlags)
        {
            output.Human($"  {name}  {description}");
        }
    }
    private static void WriteTarget(Utf8JsonWriter writer, TargetDoc entry)
    {
        writer.WriteStartObject();
        writer.WriteString("name", entry.Name);
        writer.WriteString("summary", entry.Summary);
        writer.WriteStartArray("dependsOn");
        foreach (var dep in entry.DependsOn)
        {
            writer.WriteStringValue(dep);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("parameters");
        foreach (var param in entry.Parameters)
        {
            writer.WriteStartObject();
            writer.WriteString("name", param.Name);
            writer.WriteString("type", param.Type);
            if (param.Default is not null)
            {
                writer.WriteString("default", param.Default);
            }
            writer.WriteString("description", param.Description);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("outputs");
        foreach (var outp in entry.Outputs)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", outp.Kind);
            if (outp.Path is not null)
            {
                writer.WriteString("path", outp.Path);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("examples");
        foreach (var example in entry.Examples)
        {
            writer.WriteStringValue(example);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("agentHints");
        foreach (var hint in entry.AgentHints)
        {
            writer.WriteStringValue(hint);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    private static void Verify(TargetDoc[] docs, List<ReflectedTarget> reflected)
    {
        var docNames = docs.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var reflNames = reflected.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var problems = new StringBuilder();
        var missingInCatalog = reflNames.Except(docNames, StringComparer.Ordinal).ToList();
        var missingInReflection = docNames.Except(reflNames, StringComparer.Ordinal).ToList();
        if (missingInCatalog.Count > 0 || missingInReflection.Count > 0)
        {
            problems.AppendLine("Target name sets diverged between TargetCatalog and Build:");
            if (missingInCatalog.Count > 0)
            {
                problems.AppendLine($"  only in reflection (add to catalog): {string.Join(", ", missingInCatalog)}");
            }
            if (missingInReflection.Count > 0)
            {
                problems.AppendLine($"  only in catalog (remove or rename): {string.Join(", ", missingInReflection)}");
            }
        }
        foreach (var doc in docs)
        {
            var mirror = reflected.FirstOrDefault(r => r.Name.Equals(doc.Name, StringComparison.Ordinal));
            if (mirror is null)
            {
                continue;
            }
            CompareEdges(problems, doc.Name, "dependsOn", doc.DependsOn, mirror.ExecutionDependencies);
            CompareEdges(problems, doc.Name, "before", doc.Before, mirror.OrderDependencies);
        }
        if (problems.Length > 0)
        {
            throw new InvalidOperationException(
                "TargetCatalog is out of sync with the build definition.\n" + problems);
        }
    }
    private static void CompareEdges(StringBuilder problems, string target, string edgeKind, string[] documented, IReadOnlyList<string> actual)
    {
        var docSet = documented.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actSet = actual.OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (docSet.SequenceEqual(actSet, StringComparer.Ordinal))
        {
            return;
        }
        problems.AppendLine($"  {target}.{edgeKind}: catalog=[{string.Join(", ", docSet)}] reflection=[{string.Join(", ", actSet)}]");
    }
    internal static List<ReflectedTarget> ReflectExecutableTargets(NukeBuild build)
    {
        var property = typeof(NukeBuild).GetProperty(
            "ExecutableTargets",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var executables = property?.GetValue(build) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException(
                "NUKE did not expose NukeBuild.ExecutableTargets — the catalog reconciliation " +
                "cannot run. This usually means the NUKE major version changed its internals; " +
                "update Meta/TargetCatalog.cs accordingly.");
        var result = new List<ReflectedTarget>();
        foreach (var item in executables)
        {
            var type = item.GetType();
            result.Add(new ReflectedTarget(
                ReadName(type, item, "Name"),
                ReadNames(type, item, "ExecutionDependencies"),
                ReadNames(type, item, "OrderDependencies"),
                ReadNames(type, item, "TriggerDependencies")));
        }
        return result;
    }
    private static string ReadName(Type type, object instance, string propertyName)
    {
        var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"NUKE type {type.Name} lost its '{propertyName}' member.");
        return prop.GetValue(instance)?.ToString() ?? string.Empty;
    }
    private static IReadOnlyList<string> ReadNames(Type type, object instance, string propertyName)
    {
        var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"NUKE type {type.Name} lost its '{propertyName}' member.");
        if (prop.GetValue(instance) is not System.Collections.IEnumerable items)
        {
            return [];
        }
        var names = new List<string>();
        foreach (var item in items)
        {
            names.Add(ReadName(item.GetType(), item, "Name"));
        }
        return names;
    }
    internal sealed record ReflectedTarget(
        string Name,
        IReadOnlyList<string> ExecutionDependencies,
        IReadOnlyList<string> OrderDependencies,
        IReadOnlyList<string> TriggerDependencies);
}
