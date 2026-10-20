namespace Harbor.Hosting.Tests;

/// <summary>
///     An <see cref="IHttpClientFactory" /> that remembers which named clients
///     it was asked for — the half of the real factory a composition test can
///     read.
/// </summary>
/// <remarks>
///     <para>
///         The desktop discovery resolves the factory from the container, so
///         registering this instance BEFORE <c>AddHarbor</c> is what puts it
///         in front of the default one (<c>AddHttpClient</c> registers the
///         default with <c>TryAdd</c>, so the first registration wins).
///     </para>
///     <para>
///         Shared by the two composition suites that read the names: the
///         desktop one (which providers take their client from the factory)
///         and the CLI one (which named client the ollama provider is served
///         over — #1501).
///     </para>
/// </remarks>
internal sealed class RecordingHttpClientFactory : IHttpClientFactory
{
    public List<string> Names { get; } = new();

    public HttpClient CreateClient(string name)
    {
        Names.Add(name);
        return new HttpClient();
    }
}
