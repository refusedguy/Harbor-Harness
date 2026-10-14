using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.App.Cli.Repl;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Streaming;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Mini one-line permission prompt ([steal/opencode] #1182, epic #1155):
///     <c>y</c> allows, <c>a</c> allows persistently, everything else —
///     EOF, empty line, unknown text — denies fail-closed. The printed line
///     is the router's footer density, single-line and width-capped.
/// </summary>
public class PlainPermissionAskerTests
{
    private sealed class ScriptedRenderer : ITuiRenderer
    {
        private readonly Queue<string?> _answers;

        public ScriptedRenderer(params string?[] answers) => _answers = new Queue<string?>(answers);

        public List<string?> Written { get; } = new();

        public List<string> Prompts { get; } = new();

        public ITuiRenderContext Context { get; } = new CaptureRenderContext();

        public ViewRegistry Views { get; } = new();

        public ViewModelRegistry ViewModels { get; } = new();

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task RenderAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            string? next = _answers.Count > 0 ? _answers.Dequeue() : null;
            return Task.FromResult(Maybe.From(next));
        }

        public Task<Result> WriteAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default)
        {
            Written.Add(text);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() { }
    }

    private static PermissionRequest Request(string tool, string json) => new(
        tool, "*", JsonDocument.Parse(json).RootElement.Clone(), ["allow", "deny"]);

    [Test]
    public async Task Yes_AllowsWithoutPersist()
    {
        var renderer = new ScriptedRenderer("y");
        var asker = new PlainPermissionAsker(renderer);

        var response = await asker.AskAsync(Request("bash", "{\"command\":\"make build\"}"), CancellationToken.None);

        await Assert.That(response.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(response.PersistDecision).IsFalse();
    }

    [Test]
    public async Task Always_AllowsWithPersist()
    {
        var renderer = new ScriptedRenderer("a");
        var asker = new PlainPermissionAsker(renderer);

        var response = await asker.AskAsync(Request("bash", "{\"command\":\"make build\"}"), CancellationToken.None);

        await Assert.That(response.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(response.PersistDecision).IsTrue();
    }

    [Test]
    public async Task No_DeniesWithoutPersist()
    {
        var renderer = new ScriptedRenderer("n");
        var asker = new PlainPermissionAsker(renderer);

        var response = await asker.AskAsync(Request("bash", "{\"command\":\"rm -rf /\"}"), CancellationToken.None);

        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
        await Assert.That(response.PersistDecision).IsFalse();
    }

    [Test]
    public async Task EmptyLine_DeniesFailClosed()
    {
        var renderer = new ScriptedRenderer(string.Empty);
        var asker = new PlainPermissionAsker(renderer);

        var response = await asker.AskAsync(Request("bash", "{\"command\":\"make build\"}"), CancellationToken.None);

        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task Eof_DeniesFailClosed()
    {
        var renderer = new ScriptedRenderer(new string?[] { null });
        var asker = new PlainPermissionAsker(renderer);

        var response = await asker.AskAsync(Request("bash", "{\"command\":\"make build\"}"), CancellationToken.None);

        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task UnknownText_DeniesFailClosed()
    {
        var renderer = new ScriptedRenderer("later");
        var asker = new PlainPermissionAsker(renderer);

        var response = await asker.AskAsync(Request("bash", "{\"command\":\"make build\"}"), CancellationToken.None);

        await Assert.That(response.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task PromptLine_IsSingleWidthCappedMiniLine()
    {
        var renderer = new ScriptedRenderer("y");
        var asker = new PlainPermissionAsker(renderer);

        await asker.AskAsync(Request("bash", "{\"command\":\"make build\"}"), CancellationToken.None);

        await Assert.That(renderer.Written.Count).IsEqualTo(1);
        string line = renderer.Written[0] ?? string.Empty;
        await Assert.That(line.Contains("[y/a/n]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains('\n')).IsFalse();
        await Assert.That(line.Length).IsLessThanOrEqualTo(240);
    }

    [Test]
    public async Task DescribeMiniLine_ShellGate_UsesPerToolTitle()
    {
        string line = ApprovalGateRouter.DescribeMiniLine("bash", "{\"command\":\"make build\"}", mono: true);

        await Assert.That(line.StartsWith("! ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains("run shell", StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains("[y/a/n]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains('\n')).IsFalse();
    }

    [Test]
    public async Task BuildMiniLine_EditWithDiff_InlinesDiffAsText()
    {
        string line = PermissionPresentation.BuildMiniLine(
            "edit",
            "{\"path\":\"a.cs\"}",
            diffText: "+x\n-y",
            filePath: "a.cs",
            mono: true);

        await Assert.That(line.Contains("2 lines", StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains("[y/a/n]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains('\n')).IsFalse();
    }
}
