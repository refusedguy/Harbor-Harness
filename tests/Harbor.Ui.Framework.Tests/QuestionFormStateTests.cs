using System.Collections.Immutable;
using System.Text;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #2627: the question form's answers are an immutable slice of UiState,
///     not fields the widget mutates. These tests pin the get/set seam and the
///     reducer arms; the existing QuestionFormViewTests keep the paint goldens.
/// </summary>
public class QuestionFormStateTests
{
    private static QuestionItem Solo() => new("solo", "Pick:", [new("A"), new("B")]);

    [Test]
    public async Task Widget_PublishesTransitions_ToTheHostSnapshot()
    {
        var state = QuestionFormState.Create(1);
        var form = new QuestionFormView([Solo()], () => state, next => state = next);

        _ = form.HandleKey(KeyEvent.Char(new Rune(' ')));
        await Assert.That(state.Questions[0].Selected.Length).IsEqualTo(1);

        _ = form.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        await Assert.That(state.IsSubmitted).IsTrue();
        await Assert.That(state.SubmittedAnswers.Length).IsEqualTo(1);
        await Assert.That(state.SubmittedAnswers[0].SelectedLabels[0]).IsEqualTo("A");
    }

    [Test]
    public async Task Reducer_FoldsQuestionForm_AndSubmit()
    {
        var changed = QuestionFormState.Create(1);
        var state = ChatAppReducer.Update(new UiState(), new ChatAppMsg.QuestionFormChanged(changed)).State;
        await Assert.That(state.Chat.QuestionForm).IsSameReferenceAs(changed);

        var answers = ImmutableArray.Create(new QuestionAnswer(0, ImmutableArray.Create("A"), string.Empty));
        var submitted = ChatAppReducer.Update(state, new ChatAppMsg.QuestionSubmit(answers)).State;
        await Assert.That(submitted.Chat.QuestionForm.IsSubmitted).IsTrue();
        await Assert.That(submitted.Chat.QuestionForm.SubmittedAnswers.Length).IsEqualTo(1);
    }

    [Test]
    public async Task ClearTranscript_DropsTheQuestionForm()
    {
        var submitted = QuestionFormState.Create(1).Submit(ImmutableArray<QuestionAnswer>.Empty);
        var state = ChatAppReducer.Update(new UiState(), new ChatAppMsg.QuestionFormChanged(submitted)).State;
        var cleared = ChatAppReducer.Update(state, new AppMsg.KeyInput(ChatAction.Clear, new UiKey(UiKeyCode.None))).State;
        await Assert.That(cleared.Chat.QuestionForm.IsSubmitted).IsFalse();
    }
}
