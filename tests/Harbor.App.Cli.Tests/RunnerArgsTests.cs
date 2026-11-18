using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2676 item 133: the three runner flags are global, stripped before verb
///     dispatch, and the dotenv overlay is parsed without touching the process
///     environment. Parsing is pure; only <see cref="RunnerArgs.Apply" /> writes
///     environment variables, and the one test that calls it restores them.
/// </summary>
public class RunnerArgsTests
{
    [Test]
    public async Task Extract_NoRunnerFlags_LeavesArgsUntouched()
    {
        var result = RunnerArgs.Extract(["sessions", "list", "--since", "7d"]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(string.Join('|', result.Value.Remaining)).IsEqualTo("sessions|list|--since|7d");
        await Assert.That(result.Value.Id).IsNull();
        await Assert.That(result.Value.Directory).IsNull();
        await Assert.That(result.Value.EnvFile).IsNull();
    }

    [Test]
    public async Task Extract_SeparateValueForm_StripsFlagAndValue()
    {
        var result = RunnerArgs.Extract(
            ["--runner-id", "runner-7", "--runner-dir", "r7", "--amp-env", "r7/secrets.env", "headless"]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Id).IsEqualTo("runner-7");
        await Assert.That(result.Value.Directory).IsEqualTo(Path.GetFullPath("r7"));
        await Assert.That(result.Value.EnvFile).IsEqualTo(Path.GetFullPath("r7/secrets.env"));
        await Assert.That(string.Join('|', result.Value.Remaining)).IsEqualTo("headless");
    }

    [Test]
    public async Task Extract_InlineForm_StripsFlagAndValue()
    {
        var result = RunnerArgs.Extract(["--runner-id=box-1", "ask", "hello"]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Id).IsEqualTo("box-1");
        await Assert.That(string.Join('|', result.Value.Remaining)).IsEqualTo("ask|hello");
    }

    [Test]
    public async Task Extract_FlagWithoutValue_FailsClosed()
    {
        var result = RunnerArgs.Extract(["headless", "--runner-id"]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("--runner-id requires a value");
    }

    [Test]
    public async Task Extract_EmptyValue_FailsClosed()
    {
        var result = RunnerArgs.Extract(["--amp-env="]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("--amp-env requires a non-empty value");
    }

    [Test]
    public async Task Extract_ControlCharacterInValue_FailsClosed()
    {
        var result = RunnerArgs.Extract(["--runner-id", "bad\nid"]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("control character");
    }

    [Test]
    public async Task ParseEnvFile_CommentsExportAndQuotes_ProducePairs()
    {
        var result = RunnerArgs.ParseEnvFile("""
            # injected by the runner
            KILO_API_KEY="klo_secret"

            export HARBOR_LOGLEVEL = 'Warning'
            """);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(2);
        await Assert.That(result.Value[0].Key).IsEqualTo("KILO_API_KEY");
        await Assert.That(result.Value[0].Value).IsEqualTo("klo_secret");
        await Assert.That(result.Value[1].Key).IsEqualTo("HARBOR_LOGLEVEL");
        await Assert.That(result.Value[1].Value).IsEqualTo("Warning");
    }

    [Test]
    public async Task ParseEnvFile_MalformedLine_NamesLineNumber()
    {
        var result = RunnerArgs.ParseEnvFile("GOOD=1\nnot-an-assignment\n");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("line 2");
    }

    [Test]
    public async Task ParseEnvFile_InvalidName_Fails()
    {
        var result = RunnerArgs.ParseEnvFile("1BAD=value\n");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("invalid variable name");
    }

    [Test]
    public async Task ParseEnvFile_EmptyContent_ReturnsNoPairs()
    {
        var result = RunnerArgs.ParseEnvFile("# only a comment\n\n");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Apply_EnvFileOverlay_SetsOnlyDeclaredKey()
    {
        string key = "HARBOR_RUNNER_TEST_" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), key + ".env");
        string? previous = Environment.GetEnvironmentVariable(key);
        try
        {
            File.WriteAllText(path, $"{key}=from-file\n");
            var invocation = new RunnerInvocation(null, null, path, []);

            var result = RunnerArgs.Apply(invocation);

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(Environment.GetEnvironmentVariable(key)).IsEqualTo("from-file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
