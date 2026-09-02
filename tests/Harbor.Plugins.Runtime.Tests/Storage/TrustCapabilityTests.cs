using System.Text.Json;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     Trust.json v2 per-capability approval contract: the user approves each
///     manifest-declared capability individually, the approved subset (path + sha256
///     + canonical names) is persisted, and <see cref="FileTrustPolicy.GetGrantedCapabilities" />
///     fails closed — empty for legacy v1 entries, stale hashes, unknown paths, and
///     stored grants outside the manifest.
/// </summary>
public sealed class TrustCapabilityTests : IDisposable
{
    private readonly string _root;
    private readonly string _globalDir;
    private readonly string _projectDir;
    private readonly string _store;

    public TrustCapabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbor-trust-cap-tests", Guid.NewGuid().ToString("N"));
        _globalDir = Path.Combine(_root, "global");
        _projectDir = Path.Combine(_root, "project");
        _store = Path.Combine(_globalDir, "trust.json");
        Directory.CreateDirectory(_globalDir);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException)
        { /* best-effort cleanup */
        }
    }

    private string WritePlugin(string fileName, string body)
    {
        string path = Path.Combine(_projectDir, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    private FileTrustPolicy CreatePolicy(
        Func<PluginScript, IReadOnlySet<PluginCapability>, Task<IReadOnlySet<PluginCapability>>>? capabilityPrompt = null,
        Func<PluginScript, Task<bool>>? trustPrompt = null) =>
        new(new[] { _globalDir }, _store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: trustPrompt, capabilityPrompt: capabilityPrompt);

    private static JsonElement[] ReadStoreEntries(string store)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(store));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Test]
    public async Task CapabilityPrompt_ApprovedSubset_PersistedWithPathAndHash()
    {
        const string body = "// harbor:capabilities read_files,http_requests\nclass G { }";
        string path = WritePlugin("google.cs", body);

        var approved = await CreatePolicy((_, declared) =>
            Task.FromResult<IReadOnlySet<PluginCapability>>(
                declared.Where(c => c == PluginCapability.ReadFiles).ToHashSet()))
            .DecideAsync(new PluginScript(path, body));

        await Assert.That(approved).IsEqualTo(PluginTrustDecision.Trusted);

        var entry = ReadStoreEntries(_store).Single();
        await Assert.That(entry.GetProperty("path").GetString()).IsEqualTo(Path.GetFullPath(path));
        await Assert.That(entry.GetProperty("hash").GetString()).IsEqualTo(new PluginScript(path, body).Hash);
        var persisted = entry.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()!).ToArray();
        await Assert.That(persisted).IsEquivalentTo(["read_files"]);
    }

    [Test]
    public async Task ApprovedSubset_SurvivesRestart_WithoutReprompting()
    {
        const string body = "// harbor:capabilities read_files,http_requests\nclass G { }";
        string path = WritePlugin("google.cs", body);
        int prompts = 0;

        var first = CreatePolicy((_, declared) =>
        {
            prompts++;
            return Task.FromResult<IReadOnlySet<PluginCapability>>(
                declared.Where(c => c == PluginCapability.HttpRequests).ToHashSet());
        });
        await first.DecideAsync(new PluginScript(path, body));

        // Fresh instance simulates the next app start: grants come from the store.
        var second = CreatePolicy((_, _) => { prompts++; return Task.FromResult<IReadOnlySet<PluginCapability>>(new HashSet<PluginCapability>()); });
        var granted = second.GetGrantedCapabilities(new PluginScript(path, body));
        var decision = await second.DecideAsync(new PluginScript(path, body));

        await Assert.That(decision).IsEqualTo(PluginTrustDecision.Trusted);
        await Assert.That(prompts).IsEqualTo(1); // only the first start asked
        await Assert.That(granted.Count).IsEqualTo(1);
        await Assert.That(granted.Contains(PluginCapability.HttpRequests)).IsTrue();
        await Assert.That(granted.Contains(PluginCapability.ReadFiles)).IsFalse();
    }

    [Test]
    public async Task EmptyApproval_StillTrusted_ButGrantsNothing()
    {
        const string body = "// harbor:capabilities run_processes\nclass R { }";
        string path = WritePlugin("runner.cs", body);

        var decision = await CreatePolicy((_, _) =>
                Task.FromResult<IReadOnlySet<PluginCapability>>(new HashSet<PluginCapability>()))
            .DecideAsync(new PluginScript(path, body));

        var granted = CreatePolicy().GetGrantedCapabilities(new PluginScript(path, body));

        await Assert.That(decision).IsEqualTo(PluginTrustDecision.Trusted); // loads sandboxed with zero capabilities
        await Assert.That(File.Exists(_store)).IsTrue();
        await Assert.That(ReadStoreEntries(_store).Single().GetProperty("capabilities").GetArrayLength()).IsEqualTo(0);
        await Assert.That(granted).IsEmpty();
    }

    [Test]
    public async Task LegacyV1Entry_WithoutStoredCapabilities_GrantsNothing()
    {
        const string body = "// harbor:capabilities read_files\nclass L { }";
        string path = WritePlugin("legacy.cs", body);
        var script = new PluginScript(path, body);
        File.WriteAllText(_store, $$"""
            [
              {
                "path": "{{JsonSerializer.Serialize(Path.GetFullPath(path)).Trim('"')}}",
                "hash": "{{script.Hash}}"
              }
            ]
            """);

        var granted = CreatePolicy().GetGrantedCapabilities(script);

        await Assert.That(granted).IsEmpty();
    }

    [Test]
    public async Task GetGrantedCapabilities_StaleHashOrUnknownPath_GrantsNothing()
    {
        const string bodyV1 = "// harbor:capabilities read_files\nclass S { }";
        string path = WritePlugin("stale.cs", bodyV1);
        await CreatePolicy((_, declared) => Task.FromResult(declared))
            .DecideAsync(new PluginScript(path, bodyV1));

        const string bodyV2 = "// harbor:capabilities read_files\nclass S { } // patched";
        File.WriteAllText(path, bodyV2);

        var stale = CreatePolicy().GetGrantedCapabilities(new PluginScript(path, bodyV2));
        var unknown = CreatePolicy().GetGrantedCapabilities(new PluginScript(Path.Combine(_projectDir, "ghost.cs"), bodyV1));

        await Assert.That(stale).IsEmpty(); // edited file → re-approval required
        await Assert.That(unknown).IsEmpty();
    }

    [Test]
    public async Task StoredGrantOutsideCurrentManifest_IsDropped()
    {
        // Hand-crafted store: entry claims run_processes but the script only declares read_files.
        const string body = "// harbor:capabilities read_files\nclass X { }";
        string path = WritePlugin("overgrant.cs", body);
        var script = new PluginScript(path, body);
        File.WriteAllText(_store, $$"""
            [
              {
                "path": "{{JsonSerializer.Serialize(Path.GetFullPath(path)).Trim('"')}}",
                "hash": "{{script.Hash}}",
                "capabilities": ["read_files", "run_processes", "http_requests"]
              }
            ]
            """);

        var granted = CreatePolicy().GetGrantedCapabilities(script);

        await Assert.That(granted.Count).IsEqualTo(1);
        await Assert.That(granted.Contains(PluginCapability.ReadFiles)).IsTrue();
        await Assert.That(granted.Contains(PluginCapability.RunProcesses)).IsFalse();
        await Assert.That(granted.Contains(PluginCapability.HttpRequests)).IsFalse();
    }
}
