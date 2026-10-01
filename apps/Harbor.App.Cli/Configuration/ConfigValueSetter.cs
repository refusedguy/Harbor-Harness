// ConfigValueSetter.cs — issue #709: the ONE table that decides what
// `/config set <key> <value>` accepts.
//
// WHY IT IS ITS OWN FILE
// ----------------------
// `set` had two implementations in this assembly, and for most of the repository's
// life they answered the same question differently:
//
//   apps/Harbor.App.Cli/Commands/ConfigCommand.cs      — the slash command. Also
//       the CLI verb's ENTIRE implementation: `ConfigVerb.RunAsync` constructs
//       this class and maps its `Result` to the exit code. One class, two
//       surfaces (`harbor config set` and the REPL's `/config set`).
//   apps/Harbor.App.Cli/Repl/Commands/ConfigCommand.cs — the CellForge palette's
//       `/config` menu, registered by `ReplCommandCatalog.CreateDefault`.
//
// Both assigned through `HarborConfig`'s property setters, which are
// `set => _ = TrySet…(value)` — they PARSE, and on failure DISCARD the value and
// null the field. The first copy then printed `✓ {key} = {value}` regardless, so
// a discarded value was reported as written; the second had already worked out
// that `maxsteps`/`costlimit` must be refused and gave them the
// `✗ Invalid MaxSteps value: '…'` treatment, while `model`/`provider`/`agent`
// got the same `✓` the other one did. One `/config` refused, the other
// confirmed, about half the same table.
//
// WHY A `Result<Action<HarborConfig>>` AND NOT A `Result`
// -------------------------------------------------------
// The decision has to be reached BEFORE the store is touched, and for two
// separate reasons:
//
//   1. `IConfigStore.UpdateAsync`'s updater is `Func<HarborConfig, HarborConfig>`.
//      It cannot report a failure, so a parse that happens inside it has nowhere
//      to put its reason. `Repl/Commands/ConfigCommand.cs` says this in a comment
//      on the code this replaced: "ROP: validate before touching the store".
//   2. `HarborConfig.TrySetModel` mutates FIRST and reports second — on a failed
//      parse it nulls `Identity.Model` and only then returns the failure. So
//      validating through the `Try*` forms inside the updater would not refuse
//      the value, it would APPLY it as a deletion: a rejected `model` would wipe
//      the user's own setting and then explain why it did.
//
// Returning the mutation rather than performing it keeps one switch, keeps the
// decision pure, and means a refused value costs no store write at all.
//
// WHY "REFUSE", NOT "TAKE THE BEST AVAILABLE"
// -------------------------------------------
// The repository already decided this, in three places, and this file does not
// invent a fourth:
//
//   * `HarborConfig.TrySetProvider` / `TrySetModel` / `TrySetAgent` exist ONLY to
//     return the parse reason ("ROP boundary #101") and had NO production caller.
//     The one caller that needed them was this table's callers.
//   * `Commands/ModelCommand.cs:81-86` parses with `ProviderId.TryCreate` and, on
//     failure, writes that parser's OWN error and returns `ConvertFailure()`.
//   * `Commands/AuthCommand.cs:57-61` is `✓` on success, `✗ Failed: …` on failure,
//     and returns the Failure.
//   * The palette's `/config` already refused `maxsteps`/`costlimit` in words.
//
// The messages below are the parsers' own, reused verbatim, so this is not a new
// vocabulary: `ModelRef.TryParse` already says "Expected 'provider/model'", which
// is exactly what a user who typed a bare id needs to be told.
//
// `/model` DOES accept a bare id and qualify it with the current provider
// (`ModelCommand.cs:143-150`). That is not a competing policy, and `ModelRef`'s
// own docs say why the two must stay apart: a bare id is free text for a command
// whose job is "switch to a model", where the provider is whatever the user
// already has. `/config set model` writes one specific config KEY. Substituting a
// provider the user never named would store a reference they did not type, and
// the `✓` line would then echo something the file does not contain.
//
// GUARDED BY tests/Harbor.Architecture.Tests/ConfigSetSingleTableTests.cs — a
// second key switch, or a second numeric guard, in either command file fails the
// build.

using System.Globalization;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;

namespace Harbor.App.Cli.Configuration;

/// <summary>
///     What <c>/config set &lt;key&gt; &lt;value&gt;</c> accepts, and the mutation
///     that stores it. One table for both <c>/config</c> surfaces.
/// </summary>
internal static class ConfigValueSetter
{
    /// <summary>
    ///     The keys <c>set</c> accepts, in the order the palette's menu lists
    ///     them. The palette's <c>set</c> submenu is checked against this list by
    ///     <c>ConfigSetSingleTableTests</c> in the architecture suite: it may not
    ///     offer a key this table has no arm for, because such a key can only
    ///     ever come back as <c>Unknown config key</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> Keys =
    [
        "model", "provider", "agent", "tui", "storage", "maxsteps", "costlimit"
    ];

    /// <summary>
    ///     Decide what <paramref name="value" /> means for <paramref name="key" />,
    ///     without touching a config. The caller applies the returned mutation to
    ///     the object the store is about to save — the only object a write may
    ///     land on — and it does so only after this has said yes.
    /// </summary>
    /// <param name="key">The config key, as the user typed it (case-insensitive).</param>
    /// <param name="value">The value, already joined if it contained spaces.</param>
    /// <returns>
    ///     Success carrying the mutation to perform, or failure carrying the
    ///     reason the value was refused. On failure NOTHING is stored, so a
    ///     refused value leaves the previous setting exactly as it was.
    /// </returns>
    internal static Result<Action<HarborConfig>> Decide(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            // `ModelRef.TryParse` is the only function in the repository written
            // to read a `provider/model` reference (see #678 and its guard,
            // `ModelRefSingleParserTests`) — so it, not a hand-rolled split, is
            // what decides this key. Its failure text already names the form.
            case "model":
            {
                var parsed = ModelRef.TryParse(value);
                return parsed.IsFailure
                    ? parsed.ConvertFailure<Action<HarborConfig>>()
                    : Result.Success<Action<HarborConfig>>(c => c.Model = value);
            }

            case "provider":
            {
                var parsed = ProviderId.TryCreate(value);
                return parsed.IsFailure
                    ? parsed.ConvertFailure<Action<HarborConfig>>()
                    : Result.Success<Action<HarborConfig>>(c => c.Provider = value);
            }

            case "agent":
            {
                var parsed = AgentName.TryCreate(value);
                return parsed.IsFailure
                    ? parsed.ConvertFailure<Action<HarborConfig>>()
                    : Result.Success<Action<HarborConfig>>(c => c.Agent = value);
            }

            // Invariant, not the ambient culture. A config value is a number that
            // goes into JSON and is read back by a deserializer that does not
            // know about the user's locale, so parsing it in theirs could store a
            // different number than the one typed: on a comma-decimal locale
            // `decimal.TryParse("10.5")` reads 105. That is a value distortion
            // under a `✓`, which is the same defect as a silent discard, so the
            // parse is pinned — the same call the palette's `/config` already
            // made for this key before the two paths were reconciled.
            case "maxsteps":
            {
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxSteps)
                    ? Result.Success<Action<HarborConfig>>(c => c.MaxSteps = maxSteps)
                    : Result.Failure<Action<HarborConfig>>(
                        $"Invalid MaxSteps value: '{value}' (expected integer)");
            }

            case "costlimit":
            {
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal limit)
                    ? Result.Success<Action<HarborConfig>>(c => c.CostLimit = limit)
                    : Result.Failure<Action<HarborConfig>>(
                        $"Invalid CostLimit value: '{value}' (expected decimal)");
            }

            // No parser, so nothing can be discarded here. `PresentationConfig`
            // only requires these to be non-empty, and adding a whitelist is a
            // policy change this table does not get to make on its own.
            case "tui":
                return Result.Success<Action<HarborConfig>>(c => c.Tui = value);

            case "storage":
                return Result.Success<Action<HarborConfig>>(c => c.Storage = value);

            default:
                // A refusal, not a message and a fall-through. The old `default`
                // arm wrote `Unknown config key: …` and then went on to print
                // `✓ {key} = {value}` and return Success, so a typo produced the
                // complaint and the confirmation together and exit 0.
                return Result.Failure<Action<HarborConfig>>($"Unknown config key: {key}");
        }
    }
}
