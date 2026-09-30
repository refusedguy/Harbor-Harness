// ConfigSaveLoadClosureTests.cs — issue #881: `SaveAsync` must not write a
// config its own `LoadAsync` refuses.
//
// THE DEFECT, IN ONE LINE
// -----------------------
// `JsonConfigStore.LoadCore` ends by binding the loaded config through
// `Validate` (ConfigStore.cs:117). `SaveCore` does not. So the store had two
// different answers to "is this config acceptable", and only the read path
// asked. `UpdateAsync` is where the two meet: it loads, runs the caller's
// updater over the result, and hands the outcome to `SaveAsync` — and it is the
// updater, not the store, that decides what goes in.
//
// The gap is not hypothetical. `/config set maxsteps 5000` is accepted by
// `ConfigValueSetter.Decide` — a bare `int.TryParse`, no range check; see
// #875, which deliberately left range checking out as "a separate policy
// decision" — the updater sets `MaxSteps` to 5000, and `SaveCore` writes it.
// The command prints `✓ maxsteps = 5000`. Every LATER read then fails
// `RunLimitsConfig.Validate`, which is the very method the write skipped:
//
//     $ harbor config set maxsteps 5000
//     ✓ maxsteps = 5000
//     $ harbor config
//     Error: run.maxSteps must be in [1, 1000]
//
// WHY THE STORE, NOT THE CLI
// ---------------------------
// A CLI-side range check would fix that one command and leave the property
// unenforced. The property is not "the CLI refuses bad numbers" — it is "the
// file on disk is always a file this store can read", and that is a property of
// the FILE. `SaveAsync` sits on the public `IConfigStore` interface, in
// `Harbor.Application`, and its callers live in a different assembly; the store
// cannot rely on a policy those callers happen to apply. Measured: there are
// ELEVEN production `UpdateAsync` call sites and ZERO direct production
// `SaveAsync` call sites, so the guard belongs on the method every writer
// already goes through.
//
// There is also no legitimate caller that needs to write a broken config, which
// is the usual objection to validating on write. Every production writer goes
// through `UpdateAsync`, whose load step has already returned a valid config
// before the updater runs; there is no migration writer (legacy field names are
// folded in `ConfigNormalizer` at LOAD and never written back); and no writer
// persists an intermediate state. A guard here blocks nothing that works today.
//
// WHAT THIS FILE ASSERTS
// ----------------------
// Three things, and the third is the one that stops the obvious over-correction:
//
//   1. Every config `Validate` rejects is refused by `SaveAsync`, and nothing
//      reaches the disk — the rows below are the COMPLETE enumeration of what
//      `/config set` can write and the reader then rejects.
//   2. `UpdateAsync` — the shape the CLI actually uses — leaves the previous
//      file byte-identical, so a refused value costs the user nothing.
//   3. The boundary values a valid write must STILL accept. A "fix" that made
//      `SaveAsync` reject too much would pass assertions 1 and 2 and break the
//      product; this one fails it.

using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Config.Tests;

/// <summary>
///     Issue #881: the store's read path validates and its write path does not,
///     so a successful write can leave <c>config.json</c> in a state the store
///     itself refuses to load.
/// </summary>
public sealed class ConfigSaveLoadClosureTests
{
    /// <summary>
    ///     Every value <c>/config set</c> can write that <c>HarborConfig.Validate</c>
    ///     then rejects — the complete set, one row per rule a write path can
    ///     actually reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Sourced from <c>HarborConfig.Validate</c>, which aggregates six
    ///         sections plus one rule per <c>Providers</c> entry. Four of those
    ///         rules are reachable from a <c>/config set</c> key, and each row below
    ///         names the exact message the reader will produce, so this table and
    ///         the store's own error text cannot drift apart silently.
    ///     </para>
    ///     <para>
    ///         The two rules NOT here, and why, so this table is not read as
    ///         "these were the only rules that existed":
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <c>CompactionConfig.Validate</c> (three <c>&gt; 0</c> rules).
    ///                 <c>set</c> has no compaction key — <c>ConfigValueSetter.Keys</c>
    ///                 is model/provider/agent/tui/storage/maxsteps/costlimit — so
    ///                 nothing in the product can write a bad value here.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>ProviderConfigEntry.Validate</c> (<c>apiType</c> non-empty).
    ///                 Nothing in <c>src/</c> or <c>apps/</c> ever populates
    ///                 <c>HarborConfig.Providers</c>; it is only ever READ by
    ///                 <c>ToRaw</c>. A hand-edited file can trip it, which is
    ///                 precisely the state this issue is about preventing.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    private static readonly (string Key, string Value, Action<HarborConfig> Poison, string Rule)[] PoisonedValues =
    [
        ("maxsteps", "5000", c => c.MaxSteps = 5000, "run.maxSteps must be in [1, 1000]"),
        ("maxsteps", "0", c => c.MaxSteps = 0, "run.maxSteps must be in [1, 1000]"),
        ("maxsteps", "-1", c => c.MaxSteps = -1, "run.maxSteps must be in [1, 1000]"),
        ("maxsteps", "1001", c => c.MaxSteps = 1001, "run.maxSteps must be in [1, 1000]"),
        ("costlimit", "-5", c => c.CostLimit = -5m, "cost.limit must be >= 0"),
        ("costlimit", "-0.01", c => c.CostLimit = -0.01m, "cost.limit must be >= 0"),
        ("tui", "", c => c.Tui = string.Empty, "ui.tui must not be empty"),
        ("tui", "   ", c => c.Tui = "   ", "ui.tui must not be empty"),
        ("storage", "", c => c.Storage = string.Empty, "ui.storage must not be empty"),
        ("storage", "   ", c => c.Storage = "   ", "ui.storage must not be empty")
    ];

    /// <summary>
    ///     A <c>SaveAsync</c> that succeeds but leaves a config the same store
    ///     then refuses to load is the defect, stated as a property of the pair.
    /// </summary>
    [Test]
    public async Task SaveAsync_RefusesEveryConfigLoadAsyncWouldRefuse()
    {
        foreach ((string key, string value, Action<HarborConfig> poison, string rule) in PoisonedValues)
        {
            string path = NewTempConfigPath();
            try
            {
                var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
                var config = HarborConfig.Default;
                poison(config);

                // The rule this row stands for must be the rule the reader uses.
                // If `Validate()` ever stops rejecting it, the row is stale and
                // this assertion is what says so — otherwise a later edit could
                // "fix" the test by deleting rows until it passed.
                var validation = config.Validate();
                await Assert.That(validation.IsFailure).IsTrue()
                    .Because($"`set {key} {value}` is only a defect if Validate() rejects it.");
                await Assert.That(validation.Error).IsEqualTo(rule);

                var saveResult = await store.SaveAsync(config);

                await Assert.That(saveResult.IsFailure).IsTrue()
                    .Because(
                        $"SaveAsync accepted `set {key} {value}`, so config.json now holds a value "
                        + $"LoadAsync refuses ({rule}). The command reported success and the next start fails.");
                await Assert.That(saveResult.Error).IsEqualTo(rule)
                    .Because("The write must refuse with the READER's own message, not a second wording of the rule.");

                await Assert.That(File.Exists(path)).IsFalse()
                    .Because(
                        "The refusal has to happen before the file is touched. A guard that serialized first and "
                        + "validated after would still have replaced the user's config with the broken one.");
            }
            finally
            {
                DeleteTempConfig(path);
            }
        }
    }

    /// <summary>
    ///     The shape the CLI actually uses — <c>UpdateAsync</c>, which loads,
    ///     mutates and saves — must leave the previous file exactly as it was.
    /// </summary>
    [Test]
    public async Task UpdateAsync_RefusedValue_LeavesPreviousFileByteIdentical()
    {
        string path = NewTempConfigPath();
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var seed = HarborConfig.Default;
            seed.MaxSteps = 42;
            seed.CostLimit = 3.5m;
            seed.Tui = "cellforge";
            seed.Storage = "sqlite";
            await Assert.That((await store.SaveAsync(seed)).IsSuccess).IsTrue();

            byte[] before = File.ReadAllBytes(path);

            var result = await store.UpdateAsync(c =>
            {
                c.MaxSteps = 5000;
                return c;
            });

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(before)).IsTrue()
                .Because(
                    "A refused value must cost the user nothing. The old code wrote the file and then reported "
                    + "success, so the previous setting was gone AND the new one was unreadable.");

            var loaded = await store.LoadAsync();
            await Assert.That(loaded.IsSuccess).IsTrue()
                .Because("This is the user-visible half: after a refused set, the config must still load.");
            await Assert.That(loaded.Value.MaxSteps).IsEqualTo(42);
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    /// <summary>
    ///     The other direction, and the one that keeps the guard honest: every
    ///     value the store SHOULD accept still round-trips.
    /// </summary>
    /// <remarks>
    ///     The boundaries are the point. <c>1</c> and <c>1000</c> are inside
    ///     <c>MaxSteps</c>'s inclusive range and <c>0</c> is the documented "no
    ///     limit" for <c>CostConfig</c> (its own doc comment says so), so a guard
    ///     written with <c>&lt;</c> / <c>&gt;</c> instead of <c>&lt;=</c> / <c>&gt;=</c>
    ///     — or that assumed "positive" where the rule says "non-negative" —
    ///     passes the tests above and silently stops the user configuring
    ///     anything at all.
    /// </remarks>
    [Test]
    public async Task SaveAsync_ThenLoadAsync_Succeeds_ForEveryValueInsideTheRules()
    {
        (string Label, Action<HarborConfig> Apply, Func<HarborConfig, object> ReadBack)[] accepted =
        [
            ("maxSteps lower bound", c => c.MaxSteps = 1, c => c.MaxSteps),
            ("maxSteps upper bound", c => c.MaxSteps = 1000, c => c.MaxSteps),
            ("maxSteps default", c => c.MaxSteps = 50, c => c.MaxSteps),
            ("costLimit zero (no limit)", c => c.CostLimit = 0m, c => c.CostLimit),
            ("costLimit positive", c => c.CostLimit = 10.5m, c => c.CostLimit),
            ("tui set", c => c.Tui = "cellforge", c => c.Tui),
            ("storage set", c => c.Storage = "sqlite", c => c.Storage)
        ];

        foreach ((string label, Action<HarborConfig> apply, Func<HarborConfig, object> readBack) in accepted)
        {
            string path = NewTempConfigPath();
            try
            {
                var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
                var config = HarborConfig.Default;
                apply(config);
                object expected = readBack(config);

                var saveResult = await store.SaveAsync(config);
                await Assert.That(saveResult.IsSuccess).IsTrue()
                    .Because($"`{label}` is inside the rules and must still be writable.");

                var loaded = await store.LoadAsync();
                await Assert.That(loaded.IsSuccess).IsTrue()
                    .Because($"`{label}` was written and must read back.");
                await Assert.That(readBack(loaded.Value)).IsEqualTo(expected)
                    .Because($"`{label}` survived the write but not the round trip.");
            }
            finally
            {
                DeleteTempConfig(path);
            }
        }
    }

    private static string NewTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-881-{Guid.NewGuid():N}", "config.json");

    private static void DeleteTempConfig(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
