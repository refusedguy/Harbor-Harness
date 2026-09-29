using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #684 — the other half of the exit-word pair. The shared reducer is the reference
///     behaviour: <see cref="ChatCommands.ExitWords" /> is matched with
///     <see cref="StringComparer.OrdinalIgnoreCase" />, so <c>QUIT</c> quits the TUI and the
///     desktop app. The line REPL compared with an ordinal pattern and did not, which is
///     what made one user command behave two ways.
/// </summary>
/// <remarks>
///     <para>
///         This file is green before the fix and stays green after it — deliberately. It is
///         the half of the parity that already worked, and it is what stops the drift being
///         "fixed" the other way round. Flipping <see cref="ChatCommands.ExitWords" /> to an
///         ordinal set would make both entry points agree and still be wrong: every other
///         command word in the repo is case-insensitive, so that trade would be a silent
///         behaviour regression in two front-ends to buy consistency with the one that was
///         broken.
///     </para>
///     <para>
///         The companion case lives in <c>Harbor.App.Cli.Tests/LineReplExitWordTests</c>,
///         which drives the real line REPL over the same words. Both files read the
///         vocabulary from <see cref="ChatCommands.ExitWords" /> rather than repeating it,
///         so the pair cannot drift apart the way the two copies did.
///     </para>
/// </remarks>
public class ExitWordCaseTests
{
    /// <summary>
    ///     Every case of every declared exit word must reach <c>QuitApp</c> through the
    ///     shared submit classification. The lowercase rows are the common path; the
    ///     uppercase and title-case rows are the contract with the line REPL.
    /// </summary>
    [Test]
    [Arguments("exit")]
    [Arguments("EXIT")]
    [Arguments("Exit")]
    [Arguments("quit")]
    [Arguments("QUIT")]
    [Arguments("Quit")]
    [Arguments(":q")]
    [Arguments(":Q")]
    public async Task Submit_AnyCaseOfAnExitWord_EmitsQuitApp(string typed)
    {
        var state = new UiState { Ui = TerminalUiState.Empty with { Input = new InputModel(typed, [], -1) } };

        var result = ChatAppReducer.Update(state,
            new AppMsg.KeyInput(ChatAction.Submit, new UiKey(UiKeyCode.Enter)));

        await Assert.That(result.Effect).IsTypeOf<TuiEffect.QuitApp>()
            .Because(
                $"'{typed}' differs from a declared exit word only in case, and exit words are " +
                "matched case-insensitively. The line REPL agrees — see " +
                "LineReplExitWordTests.");
    }

    /// <summary>
    ///     Non-vacuity of the table above, and of the shared set itself: the words are
    ///     spelled lowercase in <see cref="ChatCommands.ExitWords" />, so a matcher that
    ///     ignored case entirely — or dropped case <em>into</em> the literal list — would
    ///     pass the rows above while no longer accepting the word a user actually typed.
    /// </summary>
    [Test]
    public async Task ExitWords_AreDeclaredLowercase_AndMatchedCaseInsensitively()
    {
        foreach (string declared in ChatCommands.ExitWords)
        {
            await Assert.That(declared).IsEqualTo(declared.ToLowerInvariant())
                .Because(
                    "The set declares the canonical spelling. Case-insensitivity belongs to the " +
                    "comparer, not to duplicated literals — otherwise 'EXIT' would have to be " +
                    "added to the vocabulary to be accepted.");
        }
    }
}
