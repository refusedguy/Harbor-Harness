using Harbor.Abstractions.Extensions;
using Harbor.Application.Attachments;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     O(1) command registry (OrdinalIgnoreCase). Single source of truth for
///     palette commits and slash submit — replaces the two mirrored 12-branch
///     switches in CellForgeReplRunner (ExecutePaletteCommandAsync/SubmitAsync).
/// </summary>
internal sealed class ReplCommandCatalog
{
    private readonly Dictionary<string, IReplCommand> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The single threshold store set by <c>/cost-alert</c> and read by the
    ///     frame loop when a stats snapshot arrives (#3109). Injectable so the
    ///     dispatcher and the REPL palette command share one instance.
    /// </summary>
    public CostAlertMonitor CostAlerts { get; }

    public ReplCommandCatalog(CostAlertMonitor? costAlerts = null) =>
        CostAlerts = costAlerts ?? new CostAlertMonitor();

    public void Register(IReplCommand command)
    {
        Ensure.NotNull(command);
        _byId[command.Id] = command;
        foreach (string alias in command.Aliases)
            _byId[alias] = command;
    }

    public bool TryResolve(string id, out IReplCommand? command)
    {
        if (string.IsNullOrEmpty(id))
        {
            command = null;
            return false;
        }

        return _byId.TryGetValue(id, out command);
    }

    public IReadOnlyCollection<IReplCommand> GetAll() => _byId.Values.Distinct().ToArray();

    /// <summary>All palette/slash commands: one registration point (OCP).</summary>
    /// <param name="attachmentReader">
    ///     Validator behind <c>/attach</c> (issue #386). Null in tests and
    ///     catalog-only checks — the command then builds a default reader.
    /// </param>
    /// <param name="costAlerts">
    ///     Threshold store shared with the dispatcher (#3109). Null in tests
    ///     creates a private one, so catalog checks stay independent.
    /// </param>
    public static ReplCommandCatalog CreateDefault(
        ImageAttachmentReader? attachmentReader = null,
        CostAlertMonitor? costAlerts = null)
    {
        var catalog = new ReplCommandCatalog(costAlerts);
        catalog.Register(new AttachCommand(attachmentReader));
        catalog.Register(new HelpCommand());
        catalog.Register(new SetupCommand());
        catalog.Register(new VimCommand());
        catalog.Register(new ModelCommand());
        catalog.Register(new AgentCommand());
        catalog.Register(new SessionsCommand());
        catalog.Register(new SessionTreeCommand());
        // #857: the jump palette. The fourth command on the palette axis, and
        // the one Ctrl+J dispatches — the jump palette's own panel-plane route
        // is unreachable, so this is what makes the keystroke do something.
        catalog.Register(new JumpCommand());
        // Legacy text fallback registers BEFORE the panel commands so the
        // panels win "providers"/"plugins" while "permissions" stays textual.
        catalog.Register(new InfoCommand());
        catalog.Register(new ProvidersCommand());
        catalog.Register(new PluginsPanelCommand());
        catalog.Register(new PanelsCommand());
        catalog.Register(new AuthCommand());
        catalog.Register(new ConfigCommand());
        catalog.Register(new RendererCommand());
        catalog.Register(new TuiCommand());
        catalog.Register(new StorageCommand());
        catalog.Register(new NewSessionCommand());
        catalog.Register(new CostAlertCommand(catalog.CostAlerts));
        return catalog;
    }
}
