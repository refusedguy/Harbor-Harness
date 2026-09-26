namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     O(1) command registry (OrdinalIgnoreCase). Single source of truth for
///     palette commits and slash submit — replaces the two mirrored 12-branch
///     switches in CellForgeReplRunner (ExecutePaletteCommandAsync/SubmitAsync).
/// </summary>
internal sealed class ReplCommandCatalog
{
    private readonly Dictionary<string, IReplCommand> _byId = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IReplCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
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
    public static ReplCommandCatalog CreateDefault()
    {
        var catalog = new ReplCommandCatalog();
        catalog.Register(new HelpCommand());
        catalog.Register(new SetupCommand());
        catalog.Register(new VimCommand());
        catalog.Register(new ModelCommand());
        catalog.Register(new AgentCommand());
        catalog.Register(new SessionsCommand());
        catalog.Register(new SessionTreeCommand());
        catalog.Register(new AuthCommand());
        catalog.Register(new ConfigCommand());
        catalog.Register(new RendererCommand());
        catalog.Register(new TuiCommand());
        catalog.Register(new StorageCommand());
        catalog.Register(new NewSessionCommand());
        catalog.Register(new InfoCommand());
        return catalog;
    }
}
