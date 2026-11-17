namespace Harbor.Build.Meta;
/// <summary>
///     Self-check for <see cref="ImpactMapper" /> — plain assertions over a
///     throwaway sandbox tree (no TUnit dependency: the build project does
///     not reference a test framework). Runs automatically at the start of
///     the <c>what</c> target so a rules regression fails loud before any
///     answer reaches an agent.
/// </summary>
public static class ImpactMapperSelfTest
{
    private static int _scenario;
    /// <summary>Runs every scenario; throws listing the first broken expectation.</summary>
    public static void RunAll(BuildOutput output)
    {
        var sandbox = CreateSandbox();
        try
        {
            var plainInput = new ImpactInput(sandbox,
            [
                new ImpactProject("B", "src/B/B.csproj"),
                new ImpactProject("A", "src/A/A.csproj"),
                new ImpactProject("Harbor.App.Cli", "apps/Harbor.App.Cli/Harbor.App.Cli.csproj"),
                new ImpactProject("A.Tests", "tests/A.Tests/A.Tests.csproj")
            ]);
            var srcFile = Analyze(plainInput, "src/B/Thing.cs");
            Check(srcFile.Confidence == ImpactAnswer.High, "plain src change should be high confidence");
            Check(
                Names(srcFile).SequenceEqual(["B", "A", "A.Tests", "Harbor.App.Cli"], StringComparer.Ordinal),
                $"src/B closure wrong: [{string.Join(", ", Names(srcFile))}]");
            Check(Reason(srcFile, "B") == "path-inside-project-dir", "owner reason wrong");
            Check(Reason(srcFile, "Harbor.App.Cli") == "project-reference(depth=2)", "depth-2 reason wrong");
            Check(Targets(srcFile).Contains("Compile"), "plan must contain Compile for src changes");
            Check(Targets(srcFile).Contains("ArchitectureTests"), "plan must contain ArchitectureTests for src changes");
            Check(Targets(srcFile).Contains("Test"), "plan must contain Test when tests are affected");
            Check(srcFile.Skipped.Any(s => s.Command.Contains("Publish")), "Publish should be skipped by default");
            var docs = Analyze(plainInput, "docs/guide.md");
            Check(docs.Commands.Count == 0, "docs must produce an empty plan");
            Check(docs.Confidence == ImpactAnswer.High, "docs answer should be deterministic");
            var global = Analyze(plainInput, "Directory.Build.props");
            Check(
                Targets(global).SequenceEqual(["Compile", "Test", "ArchitectureTests"], StringComparer.Ordinal),
                $"global trigger must plan the full cycle, got [{string.Join(", ", Targets(global))}]");
            var buildTool = Analyze(plainInput, "build/_build/Meta/NewFile.cs");
            Check(buildTool.Commands.Count == 0 && buildTool.Notes.Count > 0,
                "build/ change must yield the self-rebuild note");
            var conditionalInput = new ImpactInput(sandbox,
            [
                new ImpactProject("B", "src/B/B.csproj"),
                new ImpactProject("Cond", "src/Cond/Cond.csproj")
            ]);
            var conditional = Analyze(conditionalInput, "src/B/Thing.cs");
            Check(conditional.Confidence == ImpactAnswer.Low,
                "closure through a conditional ProjectReference must lower confidence");
            Check(Names(conditional).Contains("Cond"), "conditional dependency must still be counted");
            var outside = Analyze(plainInput, "/etc/passwd");
            Check(outside.Confidence == ImpactAnswer.Low, "paths outside root must be low confidence");
        }
        finally
        {
            TryDelete(sandbox);
        }
        output.Info("What", $"ImpactMapper self-check passed ({_scenario} scenarios)");
    }
    private static ImpactAnswer Analyze(ImpactInput input, string path)
    {
        _scenario++;
        return ImpactMapper.Analyze(input, path, new ImpactOptions());
    }
    private static List<string> Names(ImpactAnswer answer) =>
        answer.AffectedProjects.Select(p => p.Name).ToList();
    private static string Reason(ImpactAnswer answer, string name) =>
        answer.AffectedProjects.First(p => p.Name == name).Reason;
    private static List<string> Targets(ImpactAnswer answer) =>
        answer.Commands.Select(c => c.Argv[^1]).ToList();
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"ImpactMapper self-check failed: {message}");
        }
    }
    private static string CreateSandbox()
    {
        var root = Directory.CreateTempSubdirectory("harbor-impact-selftest-").FullName;
        WriteFile(root, "src/B/B.csproj", PlainCsproj([]));
        WriteFile(root, "src/B/Thing.cs", "// marker");
        WriteFile(root, "src/A/A.csproj", PlainCsproj(["..\\B\\B.csproj"]));
        WriteFile(root, "apps/Harbor.App.Cli/Harbor.App.Cli.csproj",
            PlainCsproj(["..\\..\\src\\A\\A.csproj"]));
        WriteFile(root, "tests/A.Tests/A.Tests.csproj",
            PlainCsproj(["..\\..\\src\\A\\A.csproj"]));
        WriteFile(root, "src/Cond/Cond.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <ItemGroup Condition=\"'$(Configuration)'=='Debug'\">\n" +
            "    <ProjectReference Include=\"..\\B\\B.csproj\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n");
        WriteFile(root, "docs/guide.md", "# guide");
        return root;
    }
    private static string PlainCsproj(string[] references)
    {
        var header =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net10.0</TargetFramework>\n" +
            "  </PropertyGroup>\n";
        if (references.Length == 0)
        {
            return header + "</Project>\n";
        }
        var items = references.Aggregate(
            "  <ItemGroup>\n",
            (current, reference) => current + $"    <ProjectReference Include=\"{reference}\" />\n");
        return header + items + "  </ItemGroup>\n</Project>\n";
    }
    private static void WriteFile(string root, string relative, string content)
    {
        var path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var directory = System.IO.Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, content);
    }
    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup is best-effort; the OS removes %TEMP% eventually
        }
        catch (UnauthorizedAccessException)
        {
            // temp cleanup is best-effort; the OS removes %TEMP% eventually
        }
    }
}
