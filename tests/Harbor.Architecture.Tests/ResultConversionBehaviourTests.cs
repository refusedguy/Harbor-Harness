// ResultConversionBehaviourTests.cs — behaviour proof for the ConvertFailure wave.
//
// WHAT THIS PROVES
// ----------------
// The wave replaced 64 hand-rolled `Result.Failure<K>(x.Error)` re-types with
// `x.ConvertFailure<K>()` / `x.ConvertFailure()`. The conversion is supposed to
// be a pure spelling change, so the contract it must keep is:
//
//   1. the ERROR TEXT is byte-identical to the child's error, and
//   2. the OUTCOME (failure vs success) is unchanged.
//
// `ConvertFailure` earns its name by re-typing a failure, and it THROWS
// `InvalidOperationException` if the receiver is a success — so "the receiver is
// a failure at that point" is the invariant every one of the 64 sites rests on.
// These tests drive the real public entry points on both branches, across eight
// converted files and all three receiver shapes (`Result<T>`, value-less
// `Result`, and the `Task.FromResult` wrapper), and pin the exact strings.
//
// SCOPE
// -----
// This is a spot-check of semantics, not of coverage: the guard in
// ResultFailureConversionTests is what enforces that all 64 sites are converted.
// The pre-existing suites (HunkParserTests, HostsCatalogTests, ConfigStoreFailureTests,
// AuthStoreTests, the *RopTests store suites) already exercise many of these
// paths and must stay green — a regression there fails CI just as loudly.
//
// Each test below names the file and the converted call it pins.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Configuration;
using Harbor.Application.Sessions;
using Harbor.Diagnostics;
using Harbor.Ipc.Protocol;
using Harbor.Storage.Jsonl;
using Harbor.Telemetry;
using Harbor.Tools.Builtin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The converted sites still produce the child's error text and the same
///     success/failure outcome. See the file header for the contract.
/// </summary>
public class ResultConversionBehaviourTests
{
    /// <summary>
    ///     <c>Identifiers.cs</c> — <c>providerResult.ConvertFailure&lt;ModelRef&gt;()</c>.
    ///     The provider parse fails; the caller of <c>ModelRef.TryParse</c> must see
    ///     the provider's message, not a generic model message.
    /// </summary>
    [Test]
    public async Task ModelRef_TryParse_ProviderFailure_KeepsProviderErrorText()
    {
        Result<ModelRef> failure = ModelRef.TryParse("bad@provider/model");

        await Assert.That(failure.IsFailure).IsTrue();

        // The "(Parameter 'value')" tail is ArgumentException.Message's own, and it is
        // part of the string a caller sees: ProviderId.Create throws with nameof(value)
        // and TryCreate passes ex.Message straight through, so the re-type has to
        // carry all of it.
        await Assert.That(failure.Error)
            .IsEqualTo("Provider ID 'bad@provider' contains invalid characters (Parameter 'value')");

        // Same entry point, success branch: the conversion must not have made
        // the happy path conditional.
        Result<ModelRef> success = ModelRef.TryParse("kilocode/kilo-auto/free");
        await Assert.That(success.IsSuccess).IsTrue();
        await Assert.That(success.Value.ProviderId.Value).IsEqualTo("kilocode");
        await Assert.That(success.Value.ModelId).IsEqualTo("kilo-auto/free");
    }

    /// <summary>
    ///     <c>HarborConfig.cs</c> — the three <c>r.ConvertFailure()</c> ternaries
    ///     (TrySetProvider / TrySetModel / TrySetAgent). This is the inverted-polarity
    ///     shape: the failure is the *false* arm, so a mistake here would compile to
    ///     a throw rather than a wrong string.
    /// </summary>
    [Test]
    public async Task HarborConfig_TrySet_KeepsParseErrorText_AndStillSucceeds()
    {
        var config = HarborConfig.Default;

        // Same ArgumentException.Message tail as above — ProviderId.Create supplies
        // nameof(value), so the suffix is part of the propagated text.
        await Assert.That(config.TrySetProvider("bad@id").Error)
            .IsEqualTo("Provider ID 'bad@id' contains invalid characters (Parameter 'value')");
        await Assert.That(config.TrySetModel("bad@prov/some-model").Error)
            .IsEqualTo("Provider ID 'bad@prov' contains invalid characters (Parameter 'value')");
        await Assert.That(config.TrySetAgent("   ").Error)
            .IsEqualTo("Agent name cannot be empty");

        // A rejected value must still clear the field, exactly as before.
        await Assert.That(config.Identity.Provider).IsNull();
        await Assert.That(config.Identity.Model).IsNull();
        await Assert.That(config.Identity.Agent).IsNull();

        // …and a good value must still be stored.
        await Assert.That(config.TrySetProvider("openai").IsSuccess).IsTrue();
        await Assert.That(config.Identity.Provider?.Value).IsEqualTo("openai");
    }

    /// <summary>
    ///     <c>HunkParser.cs</c> — the only site in the repo with a TWO-hop chain:
    ///     <c>TryParseRange</c> → <c>TryParseHunkHeader</c> → <c>TryParse</c>, i.e.
    ///     <c>ConvertFailure&lt;(int,int)&gt;</c> then <c>ConvertFailure&lt;List&lt;Hunk&gt;&gt;</c>.
    ///     The innermost message has to survive both re-types untouched.
    /// </summary>
    [Test]
    public async Task HunkParser_TryParse_MalformedRange_KeepsRangeErrorThroughBothReTypes()
    {
        const string patch = "@@ -x,y +1,1 @@\n-a\n+b\n";

        Result<List<Hunk>> failure = HunkParser.TryParse(patch);

        await Assert.That(failure.IsFailure).IsTrue();
        await Assert.That(failure.Error).IsEqualTo("Malformed hunk range 'x,y' in header: @@ -x,y +1,1 @@");

        // Success branch of the same two-hop chain.
        Result<List<Hunk>> success = HunkParser.TryParse("@@ -1,1 +1,1 @@\n-a\n+b\n");
        await Assert.That(success.IsSuccess).IsTrue();
        await Assert.That(success.Value.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     <c>JsonlSessionStore.cs</c> — five of the eight converted sites, and all
    ///     three receiver shapes at once: <c>ConvertFailure&lt;Session&gt;</c>,
    ///     <c>ConvertFailure&lt;IReadOnlyList&lt;AgentMessage&gt;&gt;</c>,
    ///     <c>ConvertFailure&lt;SessionMetadata&gt;</c>, <c>ConvertFailure&lt;int&gt;</c>,
    ///     and the value-less <c>ConvertFailure()</c> — the last two reached through
    ///     <c>Task.FromResult</c>, where a lost generic argument would not fail to
    ///     compile but would change the task's element type.
    /// </summary>
    [Test]
    public async Task JsonlSessionStore_InvalidId_KeepsErrorText_OnEveryReTypedReturn()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-cf-{Guid.NewGuid():N}");
        var store = new JsonlSessionStore(root, NullLogger<JsonlSessionStore>.Instance);
        const string expected = "Invalid session id ''.";

        Result<Session> fetched = await store.GetAsync(string.Empty);
        await Assert.That(fetched.IsFailure).IsTrue();
        await Assert.That(fetched.Error).IsEqualTo(expected);

        Result<IReadOnlyList<AgentMessage>> messages = await store.GetMessagesAsync(string.Empty);
        await Assert.That(messages.IsFailure).IsTrue();
        await Assert.That(messages.Error).IsEqualTo(expected);

        Result<SessionMetadata> stats = await store.GetStatsAsync(string.Empty);
        await Assert.That(stats.IsFailure).IsTrue();
        await Assert.That(stats.Error).IsEqualTo(expected);

        Result delete = await store.DeleteAsync(string.Empty);
        await Assert.That(delete.IsFailure).IsTrue();
        await Assert.That(delete.Error).IsEqualTo(expected);

        Result<int> pruned = await store.DeleteMessagesAfterAsync(string.Empty, "m1");
        await Assert.That(pruned.IsFailure).IsTrue();
        await Assert.That(pruned.Error).IsEqualTo(expected);
    }

    /// <summary>
    ///     <c>ConfigStore.cs</c> — the one site whose source comment described the
    ///     operation in prose ("смена типа ошибки") instead of calling it. The load
    ///     failure must reach <c>GetApiKeyAsync</c>'s caller intact, and a good
    ///     config must still yield its key.
    /// </summary>
    [Test]
    public async Task JsonConfigStore_GetApiKeyAsync_KeepsLoadErrorText_AndStillReadsKeys()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-cf-{Guid.NewGuid():N}.json");
        try
        {
            // Corrupt file: the load step fails and its message must survive the
            // re-type. Only the prefix is pinned — the JsonException detail is
            // runtime-version-specific, and it is not what the conversion touches.
            File.WriteAllText(path, "{ this is not valid json !!!");
            var corrupt = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            Result<string> failed = await corrupt.GetApiKeyAsync("anthropic");
            await Assert.That(failed.IsFailure).IsTrue();
            await Assert.That(failed.Error).Contains("config.json is corrupt: ");

            // Valid file: the success branch, unchanged. The JSON is the fixture
            // JsonConfigStoreTests already proves loads cleanly.
            File.WriteAllText(path, """
                                   {
                                     "provider": "anthropic",
                                     "model": "anthropic/claude-opus-4",
                                     "agent": "code",
                                     "tui": "ansi",
                                     "storage": "jsonl",
                                     "onboarded": true,
                                     "apiKeys": {
                                       "anthropic": "sk-ant-xxx"
                                     },
                                     "maxSteps": 30,
                                     "costLimit": 5.0
                                   }
                                   """);
            var good = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            Result<string> key = await good.GetApiKeyAsync("anthropic");
            await Assert.That(key.IsSuccess).IsTrue();
            await Assert.That(key.Value).IsEqualTo("sk-ant-xxx");

            Result<string> absent = await good.GetApiKeyAsync("openai");
            await Assert.That(absent.IsFailure).IsTrue();
            await Assert.That(absent.Error).IsEqualTo("No API key for 'openai' in config.json");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     <c>HostsCatalog.cs</c> — <c>parsed.ConvertFailure()</c> feeding a
    ///     <c>List&lt;Result&gt;</c> that is later combined. If the re-type had
    ///     altered the text, the aggregate would report a different string than the
    ///     one <c>ParseEntry</c> produced.
    /// </summary>
    [Test]
    public async Task HostsCatalog_Load_BadEntry_KeepsParseErrorText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-cf-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "nuc": { "kind": "tcp", "port": 1 } }""");

            Result<IReadOnlyDictionary<string, EndpointDescriptor>> result = HostsCatalog.Load(path);

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).IsEqualTo("Host 'nuc': tcp entries require \"host\".");

            // Success branch: a well-formed entry still lands in the catalog.
            File.WriteAllText(path, """{ "local": { "kind": "uds", "path": "/tmp/harbor.sock" } }""");
            Result<IReadOnlyDictionary<string, EndpointDescriptor>> ok = HostsCatalog.Load(path);

            await Assert.That(ok.IsSuccess).IsTrue();
            await Assert.That(ok.Value.ContainsKey("local")).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     <c>InstrumentedLlmClient.cs</c> — the
    ///     <c>resolved.IsSuccess ? Success(...) : resolved.ConvertFailure&lt;ILlmClient&gt;()</c>
    ///     ternary. The decorator must pass the inner registry's error through
    ///     untouched rather than inventing one.
    /// </summary>
    [Test]
    public async Task InstrumentedProviderRegistry_GetClient_KeepsInnerErrorText()
    {
        var inner = new FailingProviderRegistry("no client for provider");
        var registry = new InstrumentedProviderRegistry(inner, new NullMetrics(), new NullTracer());

        Result<ILlmClient> client = registry.GetClient(ProviderId.Create("kilocode"));

        await Assert.That(client.IsFailure).IsTrue();
        await Assert.That(client.Error).IsEqualTo("no client for provider");
    }

    /// <summary>
    ///     <c>SessionForkService.cs</c> — <c>parentRes.ConvertFailure&lt;SessionFork&gt;()</c>,
    ///     the first of the five converted sites in the file with the most of them.
    ///     The store's error must reach the fork caller unchanged.
    /// </summary>
    [Test]
    public async Task SessionForkService_ForkAsync_ParentFailure_KeepsStoreErrorText()
    {
        var store = new FailingSessionStore("parent lookup exploded");
        var service = new SessionForkService();

        Result<SessionFork> fork = await service.ForkAsync(store, "sess-1");

        await Assert.That(fork.IsFailure).IsTrue();
        await Assert.That(fork.Error).IsEqualTo("parent lookup exploded");
    }

    // ── fakes ───────────────────────────────────────────────────────────
    //
    // Each fake fails with a sentinel string the assertions above pin. The point
    // is that the string is produced by the INNER collaborator and arrives at the
    // OUTER caller unchanged — if the conversion had rebuilt the error instead of
    // re-typing it, the sentinel would not match.

    /// <summary>An <see cref="IProviderRegistry" /> whose only reachable member fails.</summary>
    private sealed class FailingProviderRegistry(string error) : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [];

        public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Failure<ILlmClient>(error);

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<IReadOnlyList<ModelInfo>>(error));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(
            ProviderId providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<IReadOnlyList<ModelInfo>>(error));

        public void Register(ProviderId providerId, Func<ILlmClient> factory) =>
            throw new NotSupportedException();

        public Result Unregister(ProviderId providerId) => throw new NotSupportedException();

        public void AddProvider(Func<ILlmClient> factory) => throw new NotSupportedException();

        public void AddProvider(ProviderId providerId, Func<ILlmClient> factory) =>
            throw new NotSupportedException();

        public void AddProvider(string providerId, Func<ILlmClient> factory) =>
            throw new NotSupportedException();

        public void AddProvider(IProviderFactory factory) => throw new NotSupportedException();
    }

    /// <summary>An <see cref="ISessionStore" /> whose <c>GetAsync</c> fails.</summary>
    private sealed class FailingSessionStore(string error) : ISessionStore
    {
        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<Session>(error));

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<Session>>> ListAsync(
            string? projectId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> AppendMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(
            string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateStatsAsync(
            string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<int>> DeleteMessagesAfterAsync(
            string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Records nothing; the decorator is never reached on the failure path.</summary>
    private sealed class NullMetrics : IMetrics
    {
        public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags)
        {
        }

        public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags)
        {
        }
    }

    /// <summary>Records nothing; the decorator is never reached on the failure path.</summary>
    private sealed class NullTracer : ITracer
    {
        public ITelemetrySpan? StartSpan(string name, params KeyValuePair<string, object?>[] tags) => null;
    }
}
