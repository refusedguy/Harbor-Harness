using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
namespace Harbor.Abstractions.Tui;
/// <summary>
///     A single key press event.
/// </summary>
/// <remarks>
///     <para>
///         REMOVED IN #1831. The <c>IInputHandler</c> contract that used to live beside this
///         record had no implementor and no consumer anywhere in the tree: the TUI renderer
///         contract it belonged to (<c>Harbor.Abstractions.Tui.ITuiRenderer</c>) had already moved
///         to <c>Harbor.Terminal.Abstractions</c>, and the live input strategy is
///         <c>Harbor.Tui.CellForge.Engine.Input.IInputHandler</c>, a different interface with the
///         same simple name. See <c>CHANGELOG.md</c> and issue #1831.
///     </para>
///     <para>
///         The record itself stays: it is the payload of
///         <c>Harbor.Terminal.Abstractions.Views.ITuiView.HandleKey</c>.
///     </para>
/// </remarks>
/// <param name="Key">The <see cref="ConsoleKey" /> enumeration value.</param>
/// <param name="Character">The printable character, or <c>'\0'</c> if not printable.</param>
/// <param name="Modifiers">Active modifier keys (Shift, Ctrl, Alt).</param>
public sealed record KeyPress(ConsoleKey Key, char Character, ConsoleModifiers Modifiers);

/// <summary>
///     Slash-command contract.
/// </summary>
/// <remarks>
///     <para>
///         Slash commands are user-typed shortcuts prefixed with <c>/</c> (e.g. <c>/help</c>,
///         <c>/models</c>). What a command IS is the contract below; how one is ROUTED is the
///         host's concern and not this assembly's — <c>Harbor.App.Cli</c>'s
///         <c>SlashCommandDispatcher</c> owns that and hands back a <c>SlashCommandOutcome</c>
///         (#603). There is deliberately no router interface here: an unimplemented one is a
///         shape the next author copies, and a copy is a second slash-command router diverging
///         from the wired one (#565).
///     </para>
/// </remarks>
public interface ISlashCommand
{
    /// <summary>
    ///     The command name (without the leading <c>/</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     One-line description shown in <c>/help</c>.
    /// </summary>
    public string Description { get; }

    /// <summary>
    ///     Usage string (e.g. <c>/models [provider]</c>).
    /// </summary>
    public string Usage { get; }

    /// <summary>
    ///     Optional alternative names.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    ///     Optional argument suggestions shown in the command palette when
    ///     the command is selected but not yet executed. Used by the slash-
    ///     command palette to offer a second-step picker (e.g. model list,
    ///     agent list, renderer backends). Return <see langword="null" /> or
    ///     an empty list when the command takes no arguments or suggestions
    ///     are not available.
    /// </summary>
    public IReadOnlyList<string>? ArgSuggestions { get; }

    /// <summary>
    ///     Execute the command with the supplied args.
    /// </summary>
    /// <param name="args">The arguments after the command name.</param>
    /// <param name="context">The command context (session, agent, providers, tools).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default);
}

/// <summary>
///     Context for slash-command execution.
/// </summary>
public interface ICommandContext
{
    /// <summary>
    ///     The current session context.
    /// </summary>
    public ISessionContext Session { get; }

    /// <summary>
    ///     The currently-bound agent.
    /// </summary>
    public IAgent Agent { get; }

    /// <summary>
    ///     The provider registry.
    /// </summary>
    public IProviderRegistry Providers { get; }

    /// <summary>
    ///     The tool registry.
    /// </summary>
    public IToolRegistry Tools { get; }

    /// <summary>
    ///     Callback to write output to the user.
    /// </summary>
    public Action<string> Output { get; }

    /// <summary>
    ///     Callback to prompt the user for input and await the response.
    /// </summary>
    public Func<string, Task<string>> Prompt { get; }
}
