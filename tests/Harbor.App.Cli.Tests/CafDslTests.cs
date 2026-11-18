using System.Text;
using Harbor.Abstractions.Dsl;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2432: the dev-only IR-inspection verbs. The assertions read text
///     produced by the parser and the tree dumper; no CellForge renderer or
///     session is started, so the same assertions hold in a headless build.
/// </summary>
/// <remarks>
///     The bare <see cref="NotInParallelAttribute" /> is load-bearing:
///     <see cref="CafVerbTestBase.CaptureAsync" /> swaps the process-wide
///     console streams, so these tests must not race the other CAF suites.
/// </remarks>
[NotInParallel]
public class CafDslTests : CafVerbTestBase
{
    private const string Sample = """
        panel "main":
            .title "Files"
            filter_list "files":
                .height 10
                .multiple true
            text "done"
        """;

    private const string ExpectedTree = """
        node Panel@1:1 claimed=false
          op new("main")@1:1
          op title("Files")@2:6
          node FilterList@3:5 claimed=false
            op new("files")@3:5
            op height(10)@4:10
            op multiple(true)@5:10
          node Text@6:5 claimed=false
            op new("done")@6:5
        """;

    private static string WriteTempMarkup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbor-dsl-{Guid.NewGuid():N}.harbor-ui");
        File.WriteAllText(path, Sample);
        return path;
    }

    [Test]
    public async Task DslCheck_PrintIr_PrintsTheDebugTree()
    {
        string path = WriteTempMarkup();
        try
        {
            var (exit, output, error) = await RunCafAsync("dsl", ["check", "--print-ir", path]);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(error).IsEmpty();
            string actual = output.Replace("\r\n", "\n").TrimEnd();
            await Assert.That(actual).IsEqualTo(ExpectedTree);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task DslCheck_WithoutPrintIr_ReportsValidFileWithoutTree()
    {
        string path = WriteTempMarkup();
        try
        {
            var (exit, output, error) = await RunCafAsync("dsl", ["check", path]);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(output).IsEmpty();
            await Assert.That(error).IsEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task DslCheck_InvalidMarkup_ReturnsOneAndNamesTheLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"harbor-dsl-{Guid.NewGuid():N}.harbor-ui");
        File.WriteAllText(path, "not_a_widget \"x\"");
        try
        {
            var (exit, _, error) = await RunCafAsync("dsl", ["check", path]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(error).Contains("Unknown node kind");
            await Assert.That(error).Contains("(1,1)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task DslTypes_DefaultJson_EmitsEveryKindFromTheSpecKindTable()
    {
        var (exit, output, error) = await RunCafAsync("dsl", ["types"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).Contains("\"$schema\"");

        foreach (var kind in Enum.GetValues<SpecKind>())
            await Assert.That(output).Contains($"\"{SnakeCase(kind.ToString())}\"");
    }

    [Test]
    public async Task DslTypes_CSharp_EmitsTheSameKindsAsAnEnum()
    {
        var (exit, output, error) = await RunCafAsync("dsl", ["types", "--format", "csharp"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).Contains("public enum DslComponentKind");

        foreach (var kind in Enum.GetValues<SpecKind>())
            await Assert.That(output).Contains($"    {kind},");
    }

    /// <summary>
    ///     Mirrors the parser's name normalisation in the test so the emitted
    ///     schema is checked against every declared kind, not a hand-picked few.
    /// </summary>
    private static string SnakeCase(string name)
    {
        var output = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(name[index - 1]))
                output.Append('_');
            output.Append(char.ToLowerInvariant(character));
        }

        return output.ToString();
    }
}
