using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Desktop.Abstractions.Configuration;
using Harbor.Ui.Framework.Configuration;

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     Adapter that projects the Desktop.Abstractions
///     <see cref="ICommonConfigStore" /> onto the Ui.Framework
///     <see cref="ICommonConfigModelRefReader" /> contract — the read half of the
///     pair, declared in Ui.Framework.Abstractions because that project cannot
///     reference Desktop.Abstractions (the reverse edge closes a cycle).
/// </summary>
/// <remarks>
///     <para>
///         Registered as a singleton in <c>ConfigRegistration</c>, which hands
///         it the <see cref="ICommonConfigStore" /> it forwards to (that store
///         is registered a few lines above). Each
///         <see cref="ICommonConfigModelRefReader.ReadModelRefAsync" /> call is
///         one <see cref="ICommonConfigStore.LoadAsync" /> followed by one
///         <see cref="ModelRef.Qualify" />.
///     </para>
///     <para>
///         <b>No service locator (#470):</b> the adapter used to hold the whole
///         <c>IServiceProvider</c> and call <c>GetService</c> on every read,
///         which is how <c>SessionFactory</c> ended up doing a container lookup
///         per session creation. The bridge now depends on the one interface it
///         forwards to.
///     </para>
///     <para>
///         <b>No hand-written half-pair test (#453):</b> this used to read
///         <c>if (IsNullOrEmpty(cfg.DefaultProvider) || IsNullOrEmpty(cfg.DefaultModel)) return null;</c>
///         — a second, private copy of "is this pair whole?", spelled with
///         <c>||</c> where two other call sites of the same question spelled it
///         with <c>&amp;&amp;</c> and one coalesced the halves field by field. It
///         existed only because the interface returned
///         <c>(string? ProviderId, string? ModelId)?</c>, whose four spellable
///         states the type could not tell apart. <see cref="ModelRef.Qualify" />
///         is now the only place that question is answered, and a blank provider
///         half, an invalid provider id and a blank model half all arrive through
///         it as the same <c>None</c>.
///     </para>
/// </remarks>
public sealed class CommonConfigReaderAdapter : ICommonConfigModelRefReader
{
    private readonly ICommonConfigStore _store;

    /// <summary>Construct the adapter.</summary>
    /// <param name="store">The shared-config store this reader forwards to.</param>
    public CommonConfigReaderAdapter(ICommonConfigStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public async Task<Maybe<ModelRef>> ReadModelRefAsync(
        CancellationToken cancellationToken = default)
    {
        // #453: the token used to be accepted and dropped on the floor — the
        // store call below took no argument, so cancelling a session creation did
        // nothing. One optional dependency, honoured end to end.
        Result<CommonConfig> result = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure) return Maybe<ModelRef>.None;

        // Qualify is a pure re-read of the config, so there is no half to test and
        // no Result to unwrap: failure here means the config names nothing usable,
        // which is the same "not configured yet" answer as a missing file.
        Result<ModelRef> qualified = ModelRef.Qualify(
            result.Value.DefaultProvider,
            result.Value.DefaultModel);

        return qualified.IsSuccess
            ? Maybe<ModelRef>.From(qualified.Value)
            : Maybe<ModelRef>.None;
    }
}
