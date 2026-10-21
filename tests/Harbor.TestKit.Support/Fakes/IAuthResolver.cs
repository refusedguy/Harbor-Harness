using CSharpFunctionalExtensions;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit;

/// <summary>
///     An <see cref="IAuthResolver" /> that hands out a fixed key, and records
///     which providers it was asked about.
/// </summary>
/// <remarks>
///     Seven hand-written auth resolvers existed across three projects. Five
///     of them answered "here is a key" and two answered "there is none";
///     the difference between them was the whole point of the test, so both
///     answers live here as two types rather than one type with a flag nobody
///     reads.
/// </remarks>
public sealed class TestAuthResolver : IAuthResolver
{
    private readonly Lock _gate = new();
    private readonly List<string> _requested = [];

    /// <summary>
    ///     Creates a resolver that answers with a fixed key.
    /// </summary>
    /// <param name="apiKey">The key to hand out for every provider.</param>
    public TestAuthResolver(string apiKey = "test-key") => ApiKey = apiKey;

    /// <summary>The key this resolver hands out.</summary>
    public string ApiKey { get; }

    /// <summary>The providers this resolver was asked about, in order.</summary>
    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested];
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _requested.Add(providerId);
        }

        return Task.FromResult(Result.Success(ApiKey));
    }
}

/// <summary>
///     An <see cref="IAuthResolver" /> that never finds a key — the shape a
///     provider reports as an auth failure.
/// </summary>
public sealed class FailingAuthResolver : IAuthResolver
{
    private readonly Lock _gate = new();
    private readonly List<string> _requested = [];

    /// <summary>
    ///     Creates a failing resolver.
    /// </summary>
    /// <param name="message">
    ///     The failure text. The default names the provider, because a failure
    ///     message that does not say WHICH provider failed is a failure
    ///     message that cannot be acted on.
    /// </param>
    public FailingAuthResolver(string? message = null) => Message = message;

    /// <summary>The failure text used when no per-provider message is given.</summary>
    public string? Message { get; }

    /// <summary>The providers this resolver was asked about, in order.</summary>
    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested];
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _requested.Add(providerId);
        }

        string error = Message ?? $"No API key for '{providerId}'.";
        return Task.FromResult(Result.Failure<string>(error));
    }
}

/// <summary>
///     An <see cref="IAuthResolver" /> that authorizes a named set of provider
///     ids and refuses the rest.
/// </summary>
/// <remarks>
///     <para>
///         The shape of a key that arrived from outside every config file the
///         product reads: a keychain, an environment variable, a CLI override.
///         A test that needs "this provider is authorized and that one is not"
///         needs a resolver whose answer depends on the provider, not a fixed
///         key.
///     </para>
///     <para>
///         The authorized set is matched case-insensitively, because provider
///         ids reach this resolver from a configuration file on one side and
///         from a command line on the other, and the two do not agree on case.
///     </para>
/// </remarks>
public sealed class AllowListAuthResolver : IAuthResolver
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _authorized;
    private readonly List<string> _requested = [];

    /// <summary>
    ///     Creates a resolver that authorizes exactly the given provider ids.
    /// </summary>
    /// <param name="authorizedProviderIds">The provider ids that get a key.</param>
    public AllowListAuthResolver(params string[] authorizedProviderIds) =>
        _authorized = new HashSet<string>(authorizedProviderIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>The providers this resolver was asked about, in order.</summary>
    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested];
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _requested.Add(providerId);
        }

        return Task.FromResult(
            _authorized.Contains(providerId)
                ? Result.Success($"key-for-{providerId}")
                : Result.Failure<string>($"No API key for '{providerId}'."));
    }
}

/// <summary>
///     An <see cref="IAuthResolver" /> that throws, standing in for a keychain
///     that is locked or a store that is corrupt.
/// </summary>
/// <remarks>
///     A broken credential source must render a verdict, not take the window
///     down — so the product's surfaces have to survive a resolver that
///     throws, and a test can only prove that against a resolver that does.
/// </remarks>
public sealed class ThrowingAuthResolver : IAuthResolver
{
    private readonly Lock _gate = new();
    private readonly List<string> _requested = [];

    /// <summary>
    ///     Creates a resolver that throws.
    /// </summary>
    /// <param name="message">The message the failure carries.</param>
    public ThrowingAuthResolver(string message = "keychain is locked") => Message = message;

    /// <summary>The message the failure carries.</summary>
    public string Message { get; }

    /// <summary>The providers this resolver was asked about, in order.</summary>
    public IReadOnlyList<string> Requested
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested];
            }
        }
    }

    /// <inheritdoc />
    public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _requested.Add(providerId);
        }

        throw new InvalidOperationException(Message);
    }
}
