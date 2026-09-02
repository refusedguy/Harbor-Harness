using System.IO;
using Harbor.App.Cli.Commands;
using Harbor.App.Cli.Repl;

namespace Harbor.App.Cli.Tests;

public class CommandDispatchTests
{
    [Test]
    public async Task LogsCommand_HasCorrectName()
    {
        var cmd = new LogsCommand(Console.Out, Console.Error);
        await Assert.That(cmd.Name).IsEqualTo("logs");
    }

    [Test]
    public async Task DaemonCommand_HasCorrectName()
    {
        var cmd = new DaemonCommand(Console.Out, Console.Error);
        await Assert.That(cmd.Name).IsEqualTo("daemon");
    }

    [Test]
    public async Task SlashCommandDispatcher_TryHandleAsync_PropagatesSuccessExitCode()
    {
        var fake = new FakeCommand("test", exitCode: 0);
        var result = await SlashCommandDispatcher.TryHandleAsync(
            "test", Array.Empty<string>(), new ICommand[] { fake });
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task SlashCommandDispatcher_TryHandleAsync_PropagatesFailureExitCode()
    {
        // The dispatcher used to swallow the command result and always report 0;
        // it must now thread the command's own exit code through.
        var fake = new FakeCommand("test", exitCode: 42);
        var result = await SlashCommandDispatcher.TryHandleAsync(
            "test", Array.Empty<string>(), new ICommand[] { fake });
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task SlashCommandDispatcher_TryHandleAsync_PropagatesOneForFailures()
    {
        var failing = new FakeCommand("failing", exitCode: 1);
        var result = await SlashCommandDispatcher.TryHandleAsync(
            "failing", Array.Empty<string>(), new ICommand[] { failing });
        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task SlashCommandDispatcher_TryHandleAsync_ReturnsNull_WhenNoMatch()
    {
        var result = await SlashCommandDispatcher.TryHandleAsync(
            "nonexistent", Array.Empty<string>(), Array.Empty<ICommand>());
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Program_MainDispatch_UsesICommandArray()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            var swOut = new StringWriter();
            var swErr = new StringWriter();
            Console.SetOut(swOut);
            Console.SetError(swErr);

            int exitCode = await global::Harbor.App.Cli.Program.Main(new[] { "logs" });
            await Assert.That(exitCode).IsEqualTo(0);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class FakeCommand : ICommand
    {
        private readonly int _exitCode;

        public FakeCommand(string name, int exitCode = 0) => (Name, _exitCode) = (name, exitCode);

        public string Name { get; }

        public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default) => Task.FromResult(_exitCode);
    }
}
