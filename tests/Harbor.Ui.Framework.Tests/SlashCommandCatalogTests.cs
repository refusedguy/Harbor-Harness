using Harbor.Ui.Framework.Commands;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
/// Issue #462: <see cref="SlashCommandCatalog" /> is the single source of truth for the
/// slash-command vocabulary. Before it, five surfaces each owned a private copy that had
/// drifted — the palette advertised <c>/tokens</c>, <c>/diff</c>, <c>/editor</c>,
/// <c>/theme</c> and <c>/branch</c>, which the CLI dispatcher cannot run. These tests pin
/// the catalog's own invariants so a future addition cannot reintroduce that class of bug.
/// </summary>
public class SlashCommandCatalogTests
{
    [Test]
    public async Task Invocations_MatchCatalog_OneForOne()
    {
        await Assert.That(SlashCommandCatalog.Invocations.Count)
            .IsEqualTo(SlashCommandCatalog.All.Count);

        for (int i = 0; i < SlashCommandCatalog.All.Count; i++)
        {
            await Assert.That(SlashCommandCatalog.Invocations[i])
                .IsEqualTo(SlashCommandCatalog.All[i].Invocation);
        }
    }

    [Test]
    public async Task EveryEntry_HasNameDescriptionAndGroup()
    {
        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            await Assert.That(def.Name.Length).IsGreaterThan(0);
            await Assert.That(def.Description.Length).IsGreaterThan(0);
            await Assert.That(def.Group.Length).IsGreaterThan(0);
            await Assert.That(def.Invocation).IsEqualTo("/" + def.Name);
        }
    }

    /// <summary>
    /// Names and aliases share one lookup, so a duplicate would silently shadow a command.
    /// The catalog throws on construction; this pins that the shipped set is duplicate-free.
    /// </summary>
    [Test]
    public async Task NamesAndAliases_AreGloballyUnique()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();

        foreach (SlashCommandDefinition def in SlashCommandCatalog.All)
        {
            if (!seen.Add(def.Name))
            {
                duplicates.Add(def.Name);
            }

            foreach (string alias in def.Aliases)
            {
                if (!seen.Add(alias))
                {
                    duplicates.Add(alias);
                }
            }
        }

        await Assert.That(duplicates).IsEmpty();
    }

    [Test]
    public async Task Find_ResolvesCanonicalNameAliasAndUserTypedText()
    {
        await Assert.That(SlashCommandCatalog.Find("/help")!.Name).IsEqualTo("help");
        await Assert.That(SlashCommandCatalog.Find("help")!.Name).IsEqualTo("help");
        await Assert.That(SlashCommandCatalog.Find("  /HELP  ")!.Name).IsEqualTo("help");
        await Assert.That(SlashCommandCatalog.Find("h")!.Name).IsEqualTo("help");
        await Assert.That(SlashCommandCatalog.Find("quit")!.Name).IsEqualTo("exit");
        await Assert.That(SlashCommandCatalog.Find("/new-session")!.Name).IsEqualTo("new");
    }

    [Test]
    public async Task Find_UnknownAndEmpty_ReturnsNull()
    {
        await Assert.That(SlashCommandCatalog.Find("nope")).IsNull();
        await Assert.That(SlashCommandCatalog.Find(null)).IsNull();
        await Assert.That(SlashCommandCatalog.Find("")).IsNull();
        await Assert.That(SlashCommandCatalog.Find("   ")).IsNull();
    }

    /// <summary>Exactly one command ends the loop, so <c>/exit</c> stays unambiguous.</summary>
    [Test]
    public async Task ExactlyOneCommand_QuitsTheLoop()
    {
        var quitters = SlashCommandCatalog.All
            .Where(d => d.QuitsLoop)
            .Select(d => d.Name)
            .ToArray();

        await Assert.That(quitters).IsEquivalentTo(new[] { "exit" });
    }

    [Test]
    public async Task ArgSuggestions_AreExposedForArgumentTakingCommands()
    {
        await Assert.That(SlashCommandCatalog.Find("/skills")!.ArgSuggestions)
            .IsEquivalentTo(new[] { "refresh", "update" });
        await Assert.That(SlashCommandCatalog.Find("tui")!.ArgSuggestions).IsNotNull();
        await Assert.That(SlashCommandCatalog.Find("storage")!.ArgSuggestions).IsNotNull();
        await Assert.That(SlashCommandCatalog.Find("/help")!.ArgSuggestions).IsNull();
    }

    /// <summary>
    /// The three argument-taking commands keep their verbs in
    /// <c>ArgSuggestions</c>, never in <c>Aliases</c>. Listing them as aliases would
    /// make "/ansi" a top-level alias of "/tui" and "/jsonl" of "/storage" while
    /// emptying the second-step arg picker — a real bug this suite must prevent.
    /// </summary>
    [Test]
    public async Task ArgumentTakingCommands_KeepVerbsOutOfAliases()
    {
        foreach (string name in new[] { "tui", "storage", "skills" })
        {
            SlashCommandDefinition def = SlashCommandCatalog.Find(name)!;

            await Assert.That(def.ArgSuggestions).IsNotNull();
            foreach (string verb in def.ArgSuggestions!)
            {
                await Assert.That(def.Aliases.Contains(verb)).IsFalse();
                await Assert.That(SlashCommandCatalog.All.Any(d => d.Name == verb)).IsFalse();
            }
        }
    }
}
