using System.Reflection;
using System.Text.Json;
using Harbor.Providers.OpenAiCompatible.Compat;

namespace Harbor.Providers.Tests;

/// <summary>
///     #560: <see cref="ProviderCompatFlags" /> is a registry, and a registry has
///     one failure mode that a <c>switch</c> did not — a strategy you write and
///     forget to <i>register</i>. Nothing throws: the request simply goes out
///     without the field, and the provider answers with an opaque 400 that names a
///     Harbor-shaped payload rather than the missing flag.
/// </summary>
/// <remarks>
///     The registry is deliberately a hand-written array, not assembly scanning:
///     Harbor targets NativeAOT, and reflection-based discovery is banned
///     (see AGENTS.md §"What NOT to do"). A hand-written registry is only safe if
///     something checks it, which is what these tests are — two-way, so neither
///     direction can rot silently.
/// </remarks>
public class ProviderCompatFlagTests
{
    /// <summary>
    ///     Every concrete <see cref="IProviderCompatFlag" /> in the compat assembly.
    ///     Discovered by reflection, which is fine in a test: the ban is on
    ///     reflection in <i>product</i> code that must survive trimming.
    /// </summary>
    private static IReadOnlyList<Type> ConcreteFlagTypes() =>
    [
        .. typeof(IProviderCompatFlag).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsClass: true }
                        && typeof(IProviderCompatFlag).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
    ];

    /// <summary>Every flag the registry actually hands out, keyed by provider id.</summary>
    private static IReadOnlyList<IProviderCompatFlag> RegisteredFlags()
    {
        var all = new List<IProviderCompatFlag>();
        foreach (Type type in ConcreteFlagTypes())
        {
            var flag = (IProviderCompatFlag)Activator.CreateInstance(type)!;
            var matches = ProviderCompatFlags.For(flag.ProviderId);
            if (matches is not null) all.AddRange(matches);
        }

        return all;
    }

    /// <summary>
    ///     Direction 1: a flag that exists but is not in <c>_all</c> is dead code.
    ///     This is the failure #560's second pass described — silent, and shipping a
    ///     payload the provider rejects.
    /// </summary>
    [Test]
    public async Task EveryConcreteCompatFlag_IsRegistered()
    {
        var unregistered = new List<string>();

        foreach (Type type in ConcreteFlagTypes())
        {
            var flag = (IProviderCompatFlag)Activator.CreateInstance(type)!;
            var matches = ProviderCompatFlags.For(flag.ProviderId);
            bool isRegistered = matches?.Any(m => m.GetType() == type) == true;
            if (!isRegistered)
            {
                unregistered.Add($"{type.Name} (provider '{flag.ProviderId}')");
            }
        }

        await Assert.That(unregistered).IsEmpty()
            .Because(
                "these IProviderCompatFlag implementations are never handed out by "
                + "ProviderCompatFlags.For, so writing one has no effect and the request goes out "
                + "without the field: " + string.Join(", ", unregistered)
                + ". Add each to the _all array in ProviderCompatFlags.");
    }

    /// <summary>
    ///     Direction 2: a registered flag whose provider is not in the catalogue is
    ///     a typo or a deleted provider — it can never match anything, and it is the
    ///     shape a half-removed provider leaves behind.
    /// </summary>
    [Test]
    public async Task EveryRegisteredCompatFlag_TargetsABundledProvider()
    {
        var orphans = RegisteredFlags()
            .Select(f => f.ProviderId.Value)
            .Where(id => !BundledProviderIds().Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(orphans).IsEmpty()
            .Because(
                "these compat flags target provider ids that no providers/*.json declares, so they "
                + "can never be applied: " + string.Join(", ", orphans)
                + ". Either the provider config is missing or the flag's ProviderId is wrong.");
    }

    /// <summary>
    ///     Non-vacuity: the discovery step must find the flags that actually ship.
    ///     A reflection change that returned nothing would make direction 1 pass
    ///     for the wrong reason — the most expensive way this guard could fail.
    /// </summary>
    [Test]
    public async Task Discovery_FindsTheShippedFlags_AndTheCatalogueIsNonTrivial()
    {
        Type[] types = [.. ConcreteFlagTypes()];

        await Assert.That(types.Length).IsGreaterThanOrEqualTo(2)
            .Because(
                "Harbor ships DeepSeekReasonerCompatFlag and GroqMaxTokensCompatFlag; finding fewer "
                + "means the reflection broke and the registration test is vacuous.");

        await Assert.That(RegisteredFlags().Count).IsGreaterThanOrEqualTo(2)
            .Because(
                "ProviderCompatFlags.For must actually hand the shipped flags back; an empty result "
                + "would make both directions of this guard pass while the registry serves nothing.");

        await Assert.That(BundledProviderIds().Count).IsGreaterThanOrEqualTo(10)
            .Because(
                "the bundled providers/*.json catalogue is the id set the orphan check compares "
                + "against; too few means the walk broke and every flag would look like an orphan.");
    }

    /// <summary>
    ///     The bundled provider ids, read off disk. The test binary lives under
    ///     <c>tests/&lt;proj&gt;/bin/…</c>, so the repo's <c>providers/</c> directory
    ///     is a few levels up — the same upward walk every other test uses.
    /// </summary>
    private static HashSet<string> BundledProviderIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "providers");
            if (Directory.Exists(candidate))
            {
                foreach (string file in Directory.GetFiles(candidate, "*.json"))
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                        if (document.RootElement.ValueKind == JsonValueKind.Object
                            && document.RootElement.TryGetProperty("id", out JsonElement idElement)
                            && idElement.ValueKind == JsonValueKind.String
                            && idElement.GetString() is { } id)
                        {
                            ids.Add(id);
                        }
                    }
                    catch (JsonException)
                    {
                        // A malformed config is ProviderPresetCatalog's to skip and log.
                    }
                }

                return ids;
            }

            current = Path.GetDirectoryName(current);
        }

        return ids;
    }
}
