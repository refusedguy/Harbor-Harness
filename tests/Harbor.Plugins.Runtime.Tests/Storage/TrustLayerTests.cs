using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Storage;

/// <summary>
///     Tests for the trust gate between discovery and execution:
///     <see cref="TrustingPluginSource" /> + <see cref="FileTrustPolicy" />.
///     Contract: global scope is implicitly trusted, project-local scripts need a
///     persisted path+hash decision or an interactive approval, edited plugins
///     require re-approval, unknown scripts fail closed without a prompt hook.
/// </summary>
public sealed class TrustLayerTests : IDisposable
{
    private readonly string _root;
    private readonly string _globalDir;
    private readonly string _projectDir;

    public TrustLayerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbor-trust-tests", Guid.NewGuid().ToString("N"));
        _globalDir = Path.Combine(_root, "global");
        _projectDir = Path.Combine(_root, "project");
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

    private string WritePlugin(string scopeDir, string fileName, string body)
    {
        string path = Path.Combine(scopeDir, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    [Test]
    public async Task TrustingPluginSource_TrustedScripts_YieldThrough()
    {
        var fsLogger = NullLogger<FileSystemPluginSource>.Instance;
        var source = new TrustingPluginSource(
            new FileSystemPluginSource(new[] { _projectDir }, fsLogger),
            new FileTrustPolicy(
                new[] { _globalDir },
                Path.Combine(_globalDir, "trust.json"),
                NullLogger<FileTrustPolicy>.Instance),
            NullLogger<TrustingPluginSource>.Instance);

        string path = WritePlugin(_projectDir, "never.cs", "// ignored");
        await Assert.That(File.Exists(path)).IsTrue(); // sanity: fixture must exist even though it will be skipped

        int seen = 0;
        await foreach (var _ in source.GetScriptsAsync())
            seen++;
        await Assert.That(seen).IsEqualTo(0); // no prompt hook → fail closed
    }

    [Test]
    public async Task FileTrustPolicy_GlobalRoot_IsImplicitlyTrusted()
    {
        string path = WritePlugin(_globalDir, "mine.cs", SamplePluginSource.HelloWorld("Implicit"));
        var policy = new FileTrustPolicy(
            new[] { _globalDir },
            Path.Combine(_globalDir, "trust.json"),
            NullLogger<FileTrustPolicy>.Instance);

        var decision = await policy.DecideAsync(new PluginScript(path, SamplePluginSource.HelloWorld("Implicit")));

        await Assert.That(decision).IsEqualTo(PluginTrustDecision.Trusted);
    }

    [Test]
    public async Task FileTrustPolicy_NoPromptHook_FailsClosed()
    {
        string path = WritePlugin(_projectDir, "p.cs", "class P { }");
        var policy = new FileTrustPolicy(
            new[] { _globalDir },
            Path.Combine(_globalDir, "trust.json"),
            NullLogger<FileTrustPolicy>.Instance);

        var decision = await policy.DecideAsync(new PluginScript(path, "class P { }"));

        await Assert.That(decision).IsEqualTo(PluginTrustDecision.Untrusted);
    }

    [Test]
    public async Task FileTrustPolicy_PromptAccepted_DecisionPersistsAcrossInstances()
    {
        string path = WritePlugin(_projectDir, "trusted.cs", "// reviewed code v1");
        const string sourceText = "// reviewed code v1";
        string store = Path.Combine(_globalDir, "trust.json");
        bool promptedOnFirstInstance = false;

        var first = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: s => { promptedOnFirstInstance = true; return Task.FromResult(true); });
        var d1 = await first.DecideAsync(new PluginScript(path, sourceText));

        // Fresh instance simulates the next app start: decision comes from the store,
        // not from another prompt.
        int promptsOnSecondInstance = 0;
        var second = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: s => { promptsOnSecondInstance++; return Task.FromResult(true); });
        var d2 = await second.DecideAsync(new PluginScript(path, sourceText));

        await Assert.That(d1).IsEqualTo(PluginTrustDecision.Trusted);
        await Assert.That(promptedOnFirstInstance).IsTrue();
        await Assert.That(d2).IsEqualTo(PluginTrustDecision.Trusted);
        await Assert.That(promptsOnSecondInstance).IsEqualTo(0);
        await Assert.That(File.Exists(store)).IsTrue();
    }

    [Test]
    public async Task FileTrustPolicy_PromptDeclined_DoesNotPersist()
    {
        string path = WritePlugin(_projectDir, "nope.cs", "// declined");
        const string sourceText = "// declined";
        string store = Path.Combine(_globalDir, "trust.json");

        var declined = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: _ => Task.FromResult(false));
        var d1 = await declined.DecideAsync(new PluginScript(path, sourceText));

        var reopened = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: _ => Task.FromResult(false));
        var d2 = await reopened.DecideAsync(new PluginScript(path, sourceText));

        await Assert.That(d1).IsEqualTo(PluginTrustDecision.Untrusted);
        await Assert.That(d2).IsEqualTo(PluginTrustDecision.Untrusted);
        await Assert.That(File.Exists(store)).IsFalse();
    }

    [Test]
    public async Task FileTrustPolicy_EditedAfterApproval_RequiresReApproval()
    {
        string path = WritePlugin(_projectDir, "editme.cs", "// v1");
        string store = Path.Combine(_globalDir, "trust.json");
        int promptCount = 0;

        var approver = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: _ => { promptCount++; return Task.FromResult(true); });
        var accepted = await approver.DecideAsync(new PluginScript(path, "// v1"));

        File.WriteAllText(path, "// v2 — patched source");
        var verifier = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance);
        var staleVerdict = await verifier.DecideAsync(new PluginScript(path, "// v2 — patched source"));
        var reAsk = new FileTrustPolicy(
            new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
            trustPrompt: _ => { promptCount++; return Task.FromResult(true); });
        var staleWithPrompt = await reAsk.DecideAsync(new PluginScript(path, "// v2 — patched source"));

        await Assert.That(accepted).IsEqualTo(PluginTrustDecision.Trusted);
        await Assert.That(staleVerdict).IsEqualTo(PluginTrustDecision.Untrusted); // fail closed even though an old decision exists
        await Assert.That(staleWithPrompt).IsEqualTo(PluginTrustDecision.Trusted); // prompt fired again after edit
        await Assert.That(promptCount).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task TrustingPluginSource_NarrowsYieldedScriptToApprovedSubset()
    {
        const string body = "// harbor:capabilities read_files,http_requests\nclass G { }";
        WritePlugin(_projectDir, "google.cs", body);
        string store = Path.Combine(_globalDir, "trust.json");
        var source = new TrustingPluginSource(
            new FileSystemPluginSource(new[] { _projectDir }, NullLogger<FileSystemPluginSource>.Instance),
            new FileTrustPolicy(
                new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
                capabilityPrompt: (_, declared) => Task.FromResult<IReadOnlySet<PluginCapability>>(
                    declared.Where(c => c == PluginCapability.ReadFiles).ToHashSet())),
            NullLogger<TrustingPluginSource>.Instance);

        var scripts = await CollectAsync(source);

        await Assert.That(scripts).HasCount().EqualTo(1);
        var yielded = scripts[0];
        await Assert.That(yielded.DeclaredCapabilities.Count).IsEqualTo(1);
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.ReadFiles)).IsTrue();
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.HttpRequests)).IsFalse();
        // The narrowing must not touch the identity the compiler/cache keys on.
        await Assert.That(yielded.Hash).IsEqualTo(new PluginScript(yielded.Path, body).Hash);
    }

    [Test]
    public async Task TrustingPluginSource_NeverWidensBeyondManifest()
    {
        // Prompt returns capabilities the manifest never declared — the seam must
        // intersect with the declaration instead of granting the wider set.
        const string body = "// harbor:capabilities read_files\nclass X { }";
        WritePlugin(_projectDir, "over.cs", body);
        string store = Path.Combine(_globalDir, "trust.json");
        var source = new TrustingPluginSource(
            new FileSystemPluginSource(new[] { _projectDir }, NullLogger<FileSystemPluginSource>.Instance),
            new FileTrustPolicy(
                new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
                capabilityPrompt: (_, _) => Task.FromResult<IReadOnlySet<PluginCapability>>(
                    new HashSet<PluginCapability>
                    {
                        PluginCapability.ReadFiles,
                        PluginCapability.RunProcesses,
                        PluginCapability.HttpRequests,
                    })),
            NullLogger<TrustingPluginSource>.Instance);

        var scripts = await CollectAsync(source);

        await Assert.That(scripts).HasCount().EqualTo(1);
        var yielded = scripts[0];
        await Assert.That(yielded.DeclaredCapabilities.Count).IsEqualTo(1);
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.ReadFiles)).IsTrue();
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.RunProcesses)).IsFalse();
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.HttpRequests)).IsFalse();
    }

    [Test]
    public async Task TrustingPluginSource_GlobalScopeKeepsDeclaredCapabilities()
    {
        const string body = "// harbor:capabilities read_files,sub_agents\nclass M { }";
        WritePlugin(_globalDir, "mine.cs", body);
        var source = new TrustingPluginSource(
            new FileSystemPluginSource(new[] { _globalDir }, NullLogger<FileSystemPluginSource>.Instance),
            new FileTrustPolicy(
                new[] { _globalDir }, Path.Combine(_globalDir, "trust.json"), NullLogger<FileTrustPolicy>.Instance),
            NullLogger<TrustingPluginSource>.Instance);

        var scripts = await CollectAsync(source);

        var yielded = scripts.Single();
        await Assert.That(yielded.DeclaredCapabilities.Count).IsEqualTo(2);
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.ReadFiles)).IsTrue();
        await Assert.That(yielded.DeclaredCapabilities.Contains(PluginCapability.SubAgents)).IsTrue();
    }

    [Test]
    public async Task TrustingPluginSource_InvalidManifest_SkippedEvenIfPolicyTrusts()
    {
        // Unknown capability token → the plugin must be rejected regardless of the
        // policy's verdict (fail-closed contract of PluginScript.HasInvalidManifest).
        const string body = "// harbor:capabilities read_files,bogus_token\nclass B { }";
        WritePlugin(_projectDir, "bad.cs", body);
        string store = Path.Combine(_globalDir, "trust.json");
        var source = new TrustingPluginSource(
            new FileSystemPluginSource(new[] { _projectDir }, NullLogger<FileSystemPluginSource>.Instance),
            new FileTrustPolicy(
                new[] { _globalDir }, store, NullLogger<FileTrustPolicy>.Instance,
                capabilityPrompt: (_, declared) => Task.FromResult(declared)),
            NullLogger<TrustingPluginSource>.Instance);

        var scripts = await CollectAsync(source);

        await Assert.That(scripts).IsEmpty();
        // Nothing was persisted for the rejected plugin either.
        await Assert.That(File.Exists(store)).IsFalse();
    }

    private static async Task<List<PluginScript>> CollectAsync(TrustingPluginSource source)
    {
        var scripts = new List<PluginScript>();
        await foreach (var script in source.GetScriptsAsync())
            scripts.Add(script);
        return scripts;
    }
}
