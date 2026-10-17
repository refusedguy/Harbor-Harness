namespace Harbor.Application.Permissions;

/// <summary>
///     #1615: the single seam the extracted Permissions subdomain keeps to the
///     rest of the application — the persisted-permissions section of
///     <c>~/.harbor/config.json</c>. It is the section-shaped mirror of the
///     Configuration side's <c>IConfigStore</c>: same Load / load-mutate-save
///     UpdateAsync shape with the same atomicity and validate-on-write
///     semantics, projected down to just the permissions dictionary so this
///     assembly never binds the whole-config <c>HarborConfig</c> type (the
///     issue body's "1 external seam (IConfigStore + Configuration)").
/// </summary>
/// <remarks>
///     <para>
///         Harbor.Application implements this seam over its own config store —
///         <c>PermissionConfigStoreAdapter</c> wraps <c>IConfigStore</c> and is
///         what the composition roots (IntelligenceModule, CellForgeModule) pass
///         to <see cref="PermissionService" />. The service's constructor keeps
///         the parameter name <c>configStore</c>, so every call site keeps
///         compiling unchanged.
///     </para>
///     <para>
///         Implementations MUST be thread-safe, mirroring the <c>IConfigStore</c>
///         contract the underlying store honours.
///     </para>
/// </remarks>
public interface IPermissionConfigStore
{
    /// <summary>
    ///     Load the persisted permission rules section.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted rules keyed by agent name (the section
    ///     <c>HarborConfig.Permissions</c> serialises), or failure with the
    ///     underlying store's own error wording.</returns>
    Task<Result<Dictionary<string, List<PermissionRule>>>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    ///     Load → mutate → save in one atomic operation over the permissions
    ///     section only. The supplied <paramref name="updater" /> function
    ///     receives the current section and returns the new one.
    /// </summary>
    /// <param name="updater">Pure function that maps the current section to the new one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with the underlying store's own error wording.</returns>
    Task<Result> UpdateAsync(
        Func<Dictionary<string, List<PermissionRule>>, Dictionary<string, List<PermissionRule>>> updater,
        CancellationToken ct = default);
}
