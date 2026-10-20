namespace Harbor.TestKit;

/// <summary>
///     An <see cref="IServiceProvider" /> that resolves nothing.
/// </summary>
/// <remarks>
///     Four hand-written empty containers existed across two projects. The
///     shape is deliberately the SMALLEST one that satisfies
///     <see cref="IServiceProvider" />: a container that answers "no" for
///     everything, which is what a test that only needs a placeholder wants.
///     A container that threw would have been a different test.
/// </remarks>
public sealed class EmptyServiceProvider : IServiceProvider
{
    /// <inheritdoc />
    public object? GetService(Type serviceType) => null;
}
