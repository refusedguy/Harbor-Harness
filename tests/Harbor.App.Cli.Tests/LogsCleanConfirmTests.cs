using Harbor.App.Cli.Commands;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #3796: <c>harbor logs --clean</c> takes its confirmation through
///     <see cref="FormPromptState{T}.ReadAndSubmit"/> instead of hand-rolled
///     string comparisons. These tests pin the observable behaviour: y/yes
///     delete only <c>harbor-*.log</c> in the log directory; n/no/empty/EOF
///     cancel without touching a file. Input comes from an injected
///     <see cref="IFormInputSource"/>, and the log directory is a temp root,
///     so no test reads the process console or the user's real logs.
/// </summary>
public class LogsCleanConfirmTests
{
    [Test]
    [Arguments("y", true)]
    [Arguments("yes", true)]
    [Arguments("Y", true)]
    [Arguments("n", false)]
    [Arguments("no", false)]
    [Arguments("", false)]
    public async Task Clean_Answer_ConfirmsOrCancels(string answer, bool deletes)
    {
        string dir = TestTempDirs.NewDirectory("harbor-clean");
        try
        {
            string first = CreateLog(dir, "harbor-a.log");
            string second = CreateLog(dir, "harbor-b.log");
            string unrelated = Path.Combine(dir, "keep.txt");
            File.WriteAllText(unrelated, "keep me");

            var source = new QueueFormInputSource();
            source.Enqueue(answer);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var command = new LogsCommand(output, error, input: source, logDir: dir);

            int exit = await command.ExecuteAsync(["--clean"]);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(error.ToString()).IsEqualTo(string.Empty);
            string nl = Environment.NewLine;
            string expected = deletes
                ? $"About to delete 2 log file(s) from {dir}.{nl}Proceed? [y/N] Deleted 2 file(s); skipped 0 (in use or permission denied).{nl}"
                : $"About to delete 2 log file(s) from {dir}.{nl}Proceed? [y/N] Cancelled.{nl}";
            await Assert.That(output.ToString()).IsEqualTo(expected);
            if (deletes)
            {
                await Assert.That(File.Exists(first)).IsFalse();
                await Assert.That(File.Exists(second)).IsFalse();
            }
            else
            {
                await Assert.That(File.Exists(first)).IsTrue();
                await Assert.That(File.Exists(second)).IsTrue();
            }
            await Assert.That(File.Exists(unrelated)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Clean_Eof_CancelsWithoutDeleting()
    {
        string dir = TestTempDirs.NewDirectory("harbor-clean");
        try
        {
            string log = CreateLog(dir, "harbor-eof.log");
            var source = new QueueFormInputSource();
            source.Enqueue(null);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var command = new LogsCommand(output, error, input: source, logDir: dir);

            int exit = await command.ExecuteAsync(["--clean"]);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(error.ToString()).IsEqualTo(string.Empty);
            await Assert.That(output.ToString()).IsEqualTo(
                $"About to delete 1 log file(s) from {dir}.{Environment.NewLine}Proceed? [y/N] Cancelled.{Environment.NewLine}");
            await Assert.That(File.Exists(log)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateLog(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "log line");
        return path;
    }
}
