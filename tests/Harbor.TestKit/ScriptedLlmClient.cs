using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit;

/// <summary>
///     LLM client replaying scripted <see cref="LlmEvent" /> turns. Requests
///     are captured in <see cref="Requests" /> for assertions; the last script
///     repeats when the loop makes more calls than scripted.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    public static readonly ModelInfo TestModel =
        new("test-model", "test", "Test Model", 200_000, 4096, false, false, true, Pricing.Unknown, "openai");

    public static readonly ModelInfo CompactableModel =
        new("test-model", "test", "Test Model", 32_000, 1024, false, false, true, Pricing.Unknown, "openai");

    private readonly LlmEvent[][] _scripts;
    private readonly ModelInfo _model;
    private int _callIndex;

    public ScriptedLlmClient(params LlmEvent[][] scripts) : this(TestModel, scripts)
    {
    }

    public ScriptedLlmClient(ModelInfo model, params LlmEvent[][] scripts)
    {
        _model = model;
        _scripts = scripts.Length == 0 ? [[]] : scripts;
    }

    public List<LlmRequest> Requests { get; } = [];

    public List<int> RequestSizes { get; } = [];

    public int StreamCalls => _callIndex;

    public ProviderId ProviderId => ProviderId.Create("test");

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        RequestSizes.Add(request.Messages.Count);
        LlmEvent[] script = _scripts[Math.Min(_callIndex, _scripts.Length - 1)];
        _callIndex++;
        foreach (LlmEvent evt in script)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { _model }));

    public static ModelInfo CreateTestModel() => TestModel;

    public static ModelInfo CreateCompactableModel() => CompactableModel;
}

/// <summary>LLM client that always throws — for error-path tests.</summary>
public sealed class ThrowingLlmClient : ILlmClient
{
    public ProviderId ProviderId => ProviderId.Create("test");

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("stream blew up");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));
}
