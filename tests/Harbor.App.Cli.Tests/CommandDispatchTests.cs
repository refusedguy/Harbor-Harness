using System.IO;
using Harbor.Cli.Commands;
using Harbor.Cli.Repl;

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
    public async Task SlashCommandDispatcher_TryHandleAsync_DispatchesToMatchingCommand()
    {
        var fake = new FakeCommand("test");
        var result = await SlashCommandDispatcher.TryHandleAsync(
            "test", Array.Empty<string>(), new ICommand[] { fake });
        await Assert.That(result).IsEqualTo(0);
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

            int exitCode = await global::Harbor.Cli.Program.Main(new[] { "logs" });
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
        public string Name { get; }
        public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default) => Task.FromResult(42);

        public FakeCommand(string name) => Name = name;
    }
}
