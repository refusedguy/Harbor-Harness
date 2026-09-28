using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tui;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Onboarding;
using Harbor.Application.Permissions;
using Harbor.TestKit;
using Harbor.Ui.Framework.Commands;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
/// Issue #462 — the anti-drift gate. <see cref="SlashCommandDispatcher" /> is the only
/// place that can run a slash command, and <see cref="SlashCommandCatalog" /> is what every
/// palette/autocomplete/help surface advertises. When the two disagree the user is offered a
/// command that fails with "Unknown: /x". These tests fail if they ever disagree again —
/// including for a command added to the catalog without a handler, which the dispatcher
/// itself now rejects at construction time.
/// </summary>
public class SlashCommandRegistryConsistencyTests
{
    private static SlashCommandDispatcher CreateDispatcher()
    {
        var configStore = new JsonConfigStore();
        var agents = new FakeAgentRegistry();
        return new SlashCommandDispatcher(
            NullLogger<SlashCommandDispatcher>.Instance,
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(configStore, new AuthStore(configStore)),
            new PermissionService(agents, NullLogger<PermissionService>.Instance));
    }

    /// <summary>
    /// The dispatcher's construction-time check is the real guarantee; this proves it did
    /// not throw and produced a registry covering the whole catalog.
    /// </summary>
    [Test]
    public async Task Dispatcher_Constructs_AndCoversEveryCatalogCommand()
    {
        var dispatcher = CreateDispatcher();
        var advertised = dispatcher.GetRegisteredCommands();

        await Assert.That(advertised.Count).IsEqualTo(SlashCommandCatalog.All.Count);
    }

    /// <summary>
    /// Every advertised command carries a real description. The old adapter returned
    /// <c>""</c>, so the palette rendered a detail column of blanks.
    /// </summary>
    [Test]
    public async Task EveryAdvertisedCommand_HasNonEmptyDescription()
    {
        foreach (ISlashCommand cmd in CreateDispatcher().GetRegisteredCommands())
        {
            await Assert.That(cmd.Description.Length).IsGreaterThan(0);
            await Assert.That(cmd.Usage).IsEqualTo("/" + cmd.Name);
        }
    }

    /// <summary>
    /// The autocomplete vocabulary the reducer offers is the catalog, not a private list —
    /// this is what previously let <c>/clear</c> (which nothing dispatches) be suggested.
    /// </summary>
    [Test]
    public async Task AutocompleteVocabulary_EqualsCatalogInvocations()
    {
        await Assert.That(ChatCommands.Slash.ToArray())
            .IsEquivalentTo(SlashCommandCatalog.Invocations.ToArray());
    }

    /// <summary>
    /// The bug this issue reported, as a regression test. The dispatcher's own
    /// construction-time check already proves every catalog entry has a handler, so this
    /// pins the user-visible half: the non-interactive commands reach their handler instead
    /// of falling through to the "Unknown command" branch. The interactive ones
    /// (<c>/setup</c>, <c>/auth</c>, <c>/model</c>, <c>/agent</c>, <c>/config</c>,
    /// <c>/fork</c>) read stdin and are covered by their own suites; <c>/exit</c> quits.
    /// </summary>
    [Test]
    public async Task NonInteractiveCommands_DispatchInsteadOfReportingUnknown()
    {
        var dispatcher = CreateDispatcher();
        var session = Session.Create("/tmp/harbor-tests", "code", "test-provider", "test-model");
        var agent = new FakeAgent();
        var agents = new FakeAgentRegistry();
        var config = new JsonConfigStore();
        var auth = new AuthStore(config);
        var providers = new FakeProviderRegistry(new ScriptedLlmClient());

        string[] nonInteractive =
        [
            "/help", "/new", "/providers", "/sessions", "/tree",
            "/plugins", "/tui", "/storage", "/renderer", "/permissions", "/skills",
        ];

        foreach (string invocation in nonInteractive)
        {
            var written = new List<string>();
            SlashCommandOutcome outcome = await dispatcher.HandleCoreAsync(
                invocation,
                line => written.Add(line),
                _ => Task.FromResult(string.Empty),
                agent, agents, config, auth, providers, session);

            await Assert.That(outcome.ShouldQuit).IsFalse();
            await Assert.That(written.Any(l => l.StartsWith("Unknown:", StringComparison.Ordinal)))
                .IsFalse()
                .Because($"{invocation} is advertised by the palette but the dispatcher cannot run it.");
        }
    }

    /// <summary>
    /// <c>/help</c> is derived from the registry, so it can no longer advertise a command the
    /// dispatcher cannot run — its hardcoded string once omitted <c>/new</c>.
    /// </summary>
    [Test]
    public async Task Help_ListsEveryCatalogCommand()
    {
        var dispatcher = CreateDispatcher();
        var written = new List<string>();

        await dispatcher.HandleCoreAsync(
            "/help",
            line => written.Add(line),
            _ => Task.FromResult(string.Empty),
            new FakeAgent(),
            new FakeAgentRegistry(),
            new JsonConfigStore(),
            new AuthStore(new JsonConfigStore()),
            new FakeProviderRegistry(new ScriptedLlmClient()),
            Session.Create("/tmp/harbor-tests", "code", "test-provider", "test-model"));

        string help = string.Join(" ", written);
        await Assert.That(help).Contains("Commands:");
        foreach (string invocation in SlashCommandCatalog.Invocations)
        {
            await Assert.That(help).Contains(invocation);
        }
    }

    /// <summary>
    /// The same invariant from the user's side: an unknown command is the one thing the
    /// dispatcher will now never say about a command it advertises.
    /// </summary>
    [Test]
    public async Task UnknownCommand_StillReportsUnknown()
    {
        var dispatcher = CreateDispatcher();
        var written = new List<string>();

        SlashCommandOutcome outcome = await dispatcher.HandleCoreAsync(
            "/definitely-not-a-command",
            line => written.Add(line),
            _ => Task.FromResult(string.Empty),
            new FakeAgent(),
            new FakeAgentRegistry(),
            new JsonConfigStore(),
            new AuthStore(new JsonConfigStore()),
            new FakeProviderRegistry(new ScriptedLlmClient()),
            Session.Create("/tmp/harbor-tests", "code", "test-provider", "test-model"));

        await Assert.That(outcome.ShouldQuit).IsFalse();
        await Assert.That(written.Any(l => l.StartsWith("Unknown:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ArgSuggestions_ComeFromTheCatalog()
    {
        var dispatcher = CreateDispatcher();

        await Assert.That(dispatcher.GetArgSuggestions("skills"))
            .IsEquivalentTo(new[] { "refresh", "update" });
        await Assert.That(dispatcher.GetArgSuggestions("/tui")).IsNotNull();
        await Assert.That(dispatcher.GetArgSuggestions("/help")).IsNull();
        await Assert.That(dispatcher.GetArgSuggestions(string.Empty)).IsNull();
    }
}
