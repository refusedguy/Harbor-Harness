using Harbor.Application.Permissions;

namespace Harbor.Application.Configuration;

/// <summary>
///     #1615: the Application-side half of the Permissions seam. Adapts the
///     whole-config <see cref="IConfigStore" /> (atomic load/store over
///     <c>~/.harbor/config.json</c> with validate-on-write, see
///     <see cref="JsonConfigStore" />) onto the Permissions subdomain's narrow
///     <see cref="IPermissionConfigStore" /> — the permissions section only, as
///     the issue body specified ("1 external seam (IConfigStore +
///     Configuration)"). Harbor.Permissions itself never binds
///     <c>HarborConfig</c> or <c>IConfigStore</c>.
/// </summary>
/// <remarks>
///     Registered by ConfigurationModule next to <see cref="IConfigStore" />
///     itself; CompositionModule resolves the seam for PermissionService.
///     Delegation is 1:1 — no semantic change versus the pre-extraction
///     direct-IConfigStore wiring (<c>PermissionService</c> used to take
///     <see cref="IConfigStore" /> and read the same section through
///     <c>HarborConfig.Permissions</c>).
/// </remarks>
public sealed class PermissionConfigStoreAdapter(IConfigStore inner) : IPermissionConfigStore
{
    /// <inheritdoc />
    public async Task<Result<Dictionary<string, List<PermissionRule>>>> LoadAsync(CancellationToken ct = default)
    {
        // #1558: the guard + `.Value` pair is one `Map` link. `Map`
        // short-circuits on failure and carries the store's own error across
        // unchanged, so the adapter reports exactly what it reported before —
        // including the hand-built re-type it used to spell out, which the
        // architecture guard could not see because the generic argument nests
        // two levels deep.
        return (await inner.LoadAsync(ct).ConfigureAwait(false))
            .Map(config => config.Permissions);
    }

    /// <inheritdoc />
    public Task<Result> UpdateAsync(
        Func<Dictionary<string, List<PermissionRule>>, Dictionary<string, List<PermissionRule>>> updater,
        CancellationToken ct = default)
        => inner.UpdateAsync(
            config => { config.Permissions = updater(config.Permissions); return config; },
            ct);
}
