using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     Audit trail contract: <see cref="PluginAuditLog" /> is append-only JSONL at
///     <c>{harborDir}/logs/plugin-audit.jsonl</c>, survives IO problems silently, and
///     the full install flow (trust gate + first tool call) leaves exactly the
///     <c>read_files</c> line for the .cs source and one line per exercised capability.
/// </summary>
public sealed class PluginAuditLogTests : IDisposable
{
    private readonly string _root;
    private readonly string _harborDir;
    private readonly string _pluginsDir;
    private readonly string _logPath;

    public PluginAuditLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbor-audit-tests", Guid.NewGuid().ToString("N"));
        _harborDir = Path.Combine(_root, "harbor");
        _pluginsDir = Path.Combine(_harborDir, "plugins");
        Directory.CreateDirectory(_pluginsDir);
        _logPath = Path.Combine(_harborDir, "logs", "plugin-audit.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException)
        { /* best-effort cleanup */
        }
    }

    private PluginAuditLog CreateAuditLog() =>
        new(_harborDir, NullLogger<PluginAuditLog>.Instance);

    private static async Task<IReadOnlyList<JsonElement>> ReadLinesAsync(string path)
    {
        var lines = new List<JsonElement>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (line.Length == 0)
                continue;
            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return lines;
    }

    [Test]
    public async Task Write_CreatesLogsDirectory_AndAppendsOneJsonLinePerEntry()
    {
        var audit = CreateAuditLog();

        await audit.WriteAsync("web_search", PluginCapability.ReadFiles, "/tmp/p/web_search.cs", "allow");
        await audit.WriteAsync("web_search", PluginCapability.HttpRequests, "https://api.example.com", "deny", "sandbox: timeout");

        var lines = await ReadLinesAsync(_logPath);
        await Assert.That(lines).Count().IsEqualTo(2);

        await Assert.That(lines[0].GetProperty("plugin").GetString()).IsEqualTo("web_search");
        await Assert.That(lines[0].GetProperty("capability").GetString()).IsEqualTo("read_files");
        await Assert.That(lines[0].GetProperty("result").GetString()).IsEqualTo("allow");
        await Assert.That(lines[0].TryGetProperty("detail", out _)).IsFalse();

        await Assert.That(lines[1].GetProperty("capability").GetString()).IsEqualTo("http_requests");
        await Assert.That(lines[1].GetProperty("target").GetString()).IsEqualTo("https://api.example.com");
        await Assert.That(lines[1].GetProperty("result").GetString()).IsEqualTo("deny");
        await Assert.That(lines[1].GetProperty("detail").GetString()).Contains("timeout");
    }

    [Test]
    public async Task Write_NeverTruncates_PreexistingLinesSurvive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        const string seed = """{"timestamp":"2026-01-01T00:00:00.0000000Z","plugin":"old","capability":"read_env","target":"PATH","result":"allow"}""";
        await File.WriteAllTextAsync(_logPath, seed + "\n");

        var audit = CreateAuditLog();
        await audit.WriteAsync("new", PluginCapability.ReadEnv, "HOME", "allow");

        var lines = await ReadLinesAsync(_logPath);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[0].GetProperty("plugin").GetString()).IsEqualTo("old");
        await Assert.That(lines[1].GetProperty("plugin").GetString()).IsEqualTo("new");
    }

    [Test]
    public async Task Write_IoFailure_IsSwallowedNotThrown()
    {
        // 'logs' exists as a FILE — creating the log directory will fail.
        string logsAsFile = Path.Combine(_harborDir, "logs");
        await File.WriteAllTextAsync(logsAsFile, "not a directory");

        var audit = CreateAuditLog();

        // Must not throw — audit is best-effort telemetry.
        await audit.WriteAsync("web_search", PluginCapability.ReadFiles, "/tmp/p.cs", "allow");
        await Assert.That(File.Exists(logsAsFile)).IsTrue();
    }

    [Test]
    public async Task GoogleSearchInstall_AuditTrail_ContainsReadFilesOnSourceAndHttpRequestOnUrl()
    {
        var audit = CreateAuditLog();
        string pluginPath = Path.Combine(_pluginsDir, "google_search.cs");
        await File.WriteAllTextAsync(
            pluginPath,
            """
            // harbor:capabilities read_files,http_requests
            public sealed class GoogleSearchPlugin { }
            """);

        var policy = new FileTrustPolicy(
            new[] { _pluginsDir },
            Path.Combine(_harborDir, "plugins", "trust.json"),
            NullLogger<FileTrustPolicy>.Instance);
        var source = new TrustingPluginSource(
            new FileSystemPluginSource(new[] { _pluginsDir }, NullLogger<FileSystemPluginSource>.Instance),
            policy,
            NullLogger<TrustingPluginSource>.Instance,
            audit);

        PluginScript? loaded = null;
        await foreach (var script in source.GetScriptsAsync())
            loaded = script;
        await Assert.That(loaded).IsNotNull();

        // First capability use: Harbor itself reads the .cs at the trust gate.
        var lines = await ReadLinesAsync(_logPath);
        await Assert.That(lines).Count().IsEqualTo(1);
        await Assert.That(lines[0].GetProperty("capability").GetString()).IsEqualTo("read_files");
        await Assert.That(lines[0].GetProperty("target").GetString()).IsEqualTo(pluginPath);
        await Assert.That(lines[0].GetProperty("result").GetString()).IsEqualTo("allow");

        // First tool call exercises the granted http_requests capability.
        var bus = new RecordingEventBus();
        var tool = new SandboxedPluginTool(
            new FakeSearchTool(),
            "google_search",
            bus,
            NullLogger.Instance,
            capabilities: policy.GetGrantedCapabilities(loaded!),
            audit: audit);
        var ctx = new ToolContext(
            SessionId: "session-1",
            MessageId: "msg-1",
            CallId: "call-1",
            Agent: "code",
            Abort: CancellationToken.None,
            Messages: Array.Empty<AgentMessage>(),
            ReportProgress: (_, _) => Task.CompletedTask,
            Ask: (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
            Services: null!);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("""{"url":"https://www.google.com/search?q=hello"}""").RootElement.Clone(),
            ctx);

        await Assert.That(result.IsError).IsFalse();
        lines = await ReadLinesAsync(_logPath);
        await Assert.That(lines).Count().IsEqualTo(2);
        await Assert.That(lines[1].GetProperty("capability").GetString()).IsEqualTo("http_requests");
        await Assert.That(lines[1].GetProperty("target").GetString()).IsEqualTo("https://www.google.com/search?q=hello");
        await Assert.That(lines[1].GetProperty("result").GetString()).IsEqualTo("allow");
    }

    private sealed class FakeSearchTool : ITool
    {
        public ToolName Name => ToolName.Create("web_search");
        public string DisplayName => "Web Search";
        public string Description => "Fake search tool";
        public ExecutionMode ExecutionMode => ExecutionMode.Sequential;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
        public JsonDocument ParameterSchema => JsonDocument.Parse("{}");

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("results"));
    }
}
