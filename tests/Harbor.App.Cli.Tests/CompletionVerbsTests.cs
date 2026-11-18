using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2669: acceptance for <c>harbor completions &lt;shell&gt;</c>. The help
///     usage line and every generated script are projected from the same
///     <c>CliVerbCatalog</c>, so this suite pins that they stay in sync:
///     every verb help advertises must be completable in every shell.
/// </summary>
/// <remarks>
///     <c>NotInParallel</c> is load-bearing: the CAF arm swaps the
///     process-wide console, as the other <c>Caf*Tests</c> classes do.
/// </remarks>
[NotInParallel]
public class CompletionVerbsTests : CafVerbTestBase
{
    private static readonly string[] Shells = ["bash", "zsh", "fish", "powershell"];

    [Test]
    public async Task HelpUsage_IsProjectedFromCatalog()
    {
        string help = CaptureOut(() => HelpVerbs.PrintHelp());

        // The whole enumeration must come from the catalog, not a hand-written
        // second copy in the help text.
        await Assert.That(help).Contains(CliVerbCatalog.FormatUsageVerbs());
        foreach (string verb in CliVerbCatalog.All)
            await Assert.That(help).Contains(verb);
    }

    [Test]
    public async Task EachCompletionScript_ContainsEveryCatalogVerb()
    {
        string wordList = CliVerbCatalog.CompletionWordList();
        foreach (string shell in Shells)
        {
            string script = CompletionVerbs.Generate(shell);
            await Assert.That(script).IsNotEmpty();
            await Assert.That(script).Contains(wordList);
            foreach (string verb in CliVerbCatalog.All)
                await Assert.That(script).Contains(verb);
        }
    }

    [Test]
    public async Task UnsupportedShell_ProducesNoScript()
    {
        await Assert.That(CompletionVerbs.Generate("tcsh")).IsEmpty();
    }

    [Test]
    public async Task CompletionsVerb_PrintsScriptForEveryShell()
    {
        string wordList = CliVerbCatalog.CompletionWordList();
        foreach (string shell in Shells)
        {
            var (exit, output, error) = await RunCafAsync("completions", [shell]);
            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(error).IsEmpty();
            await Assert.That(output).Contains(wordList);
        }
    }

    [Test]
    public async Task CompletionsVerb_UnsupportedShell_FailsClosed()
    {
        var (exit, _, error) = await RunCafAsync("completions", ["tcsh"]);
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("unsupported shell");
    }
}
