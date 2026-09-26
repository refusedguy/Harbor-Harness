using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
namespace Harbor.Abstractions.Tui;
/// <summary>
///     Strategy interface for input handling.
/// </summary>
/// <remarks>
///     <para>
///         The input handler is the abstraction over the user's keyboard. TUI renderers receive
///         an <see cref="IInputHandler" /> implementation (e.g. raw-mode Console, custom terminal
///         library, or test double) and call <see cref="ReadKeyAsync" /> in their main loop.
///     </para>
///     <para>
///         Implementations MUST be thread-safe for <see cref="KeyPressed" /> event subscription
///         and SHOULD support cooperative cancellation via the supplied <see cref="CancellationToken" />.
///     </para>
/// </remarks>
public interface IInputHandler : IDisposable
{
    /// <summary>
    ///     Asynchronously read the next key press.
    /// </summary>
    /// <param name="ct">Cancellation token used to abort the wait.</param>
    /// <returns>Success with the <see cref="KeyPress" />, or failure if cancelled or errored.</returns>
    public Task<Result<KeyPress>> ReadKeyAsync(CancellationToken ct = default);

    /// <summary>
    ///     Event raised for each key press. Useful for push-based UIs.
    /// </summary>
    public event EventHandler<KeyPressEventArgs>? KeyPressed;
}

/// <summary>
///     A single key press event.
/// </summary>
/// <param name="Key">The <see cref="ConsoleKey" /> enumeration value.</param>
/// <param name="Character">The printable character, or <c>'\0'</c> if not printable.</param>
/// <param name="Modifiers">Active modifier keys (Shift, Ctrl, Alt).</param>
public sealed record KeyPress(ConsoleKey Key, char Character, ConsoleModifiers Modifiers);

/// <summary>
///     Event args for <see cref="IInputHandler.KeyPressed" />.
/// </summary>
public sealed class KeyPressEventArgs : EventArgs
{

    /// <summary>
    ///     Construct event args wrapping a <see cref="KeyPress" />.
    /// </summary>
    /// <param name="key">The key press to wrap.</param>
    public KeyPressEventArgs(KeyPress key)
    {
        Key = key;
    }
    /// <summary>
    ///     The key press that triggered the event.
    /// </summary>
    public KeyPress Key { get; }
}

/// <summary>
///     Slash-command contract.
/// </summary>
/// <remarks>
///     <para>
///         Slash commands are user-typed shortcuts prefixed with <c>/</c> (e.g. <c>/help</c>,
///         <c>/models</c>). They are dispatched by <see cref="ISlashCommandRouter" />.
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

/// <summary>
///     Router for slash-commands.
/// </summary>
/// <remarks>
///     Implementations live in <c>Harbor.Core</c>.
/// </remarks>
public interface ISlashCommandRouter
{
    /// <summary>
    ///     Register a slash command.
    /// </summary>
    /// <param name="command">The command to register.</param>
    /// <returns>Success, or failure if a command with the same name is already registered.</returns>
    public Result Register(ISlashCommand command);

    /// <summary>
    ///     Unregister a slash command by name.
    /// </summary>
    /// <param name="name">The command name.</param>
    /// <returns>Success, or failure if the command is not registered.</returns>
    public Result Unregister(string name);

    /// <summary>
    ///     Try to handle a user input line. If the input starts with <c>/</c>, dispatches the
    ///     matching command and returns <see langword="true" />; otherwise returns <see langword="false" />.
    /// </summary>
    /// <param name="input">The raw user input.</param>
    /// <param name="context">The command context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success with <see langword="true" /> if handled, <see langword="false" /> if not a command; failure on error.</returns>
    public Task<Result<bool>> TryHandleAsync(string input, ICommandContext context, CancellationToken ct = default);

    /// <summary>
    ///     Get all registered commands.
    /// </summary>
    /// <returns>A read-only list of registered commands.</returns>
    public IReadOnlyList<ISlashCommand> GetRegisteredCommands();
}
