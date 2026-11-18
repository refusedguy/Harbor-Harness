using Ensure = Harbor.Abstractions.Extensions.Ensure;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Host answer for one questionnaire step: an optional selected choice plus
/// freeform text. It carries no Harbor types so a host can persist the draft
/// directly and restore it with <see cref="Questionnaire.SetAnswer"/>.
/// </summary>
/// <param name="Choice">Selected choice value, or null when none is chosen.</param>
/// <param name="Text">Freeform text entered for the step.</param>
public readonly record struct QuestionnaireAnswer(string? Choice = null, string Text = "")
{
    /// <summary>True when both slots are empty.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Choice) && string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// External step definition of a <see cref="Questionnaire"/>. The host owns
/// the text, the choices and the validation rule; the widget only renders and
/// navigates them. No Harbor-specific type appears in this contract.
/// </summary>
/// <param name="Id">Stable key used for answers and errors.</param>
/// <param name="Title">Text painted in the step header.</param>
/// <param name="Description">Optional second line under the header.</param>
/// <param name="Choices">Optional choice values, painted in schema order.</param>
/// <param name="Required">When true, an empty answer fails validation.</param>
/// <param name="Freeform">When true, a single-line input row is painted.</param>
/// <param name="Validate">
/// Optional rule returning an error message, or null when the answer is valid.
/// It receives the current answer and the whole draft, so a step may validate
/// against answers given on other steps.
/// </param>
public sealed record QuestionnaireStep(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Choices = null,
    bool Required = false,
    bool Freeform = false,
    Func<QuestionnaireAnswer, IReadOnlyDictionary<string, QuestionnaireAnswer>, string?>? Validate = null)
{
    /// <summary>Choice values in schema order; empty when the step has none.</summary>
    public IReadOnlyList<string> Values { get; } = Choices ?? Array.Empty<string>();
}

/// <summary>
/// Domain-neutral multi-step wizard over cells, the CellForge counterpart of
/// the gpui-kit Questionnaire contract. Steps, answers, validation rules and
/// navigation are supplied from outside; this type owns only the current step,
/// the answer draft, validation attempts and the focused row.
/// </summary>
/// <remarks>
/// <para>
///     Back never validates and never drops the draft. Next validates the
///     current step and stays put on failure. Submit validates every step and
///     moves to the first invalid one. An external error set by the host wins
///     over the step's own rule and can be cleared once the host is satisfied.
/// </para>
/// <para>
///     State lives in the widget instance, as with the other generic widgets;
///     hosts persist <see cref="Answers"/> and restore it with
///     <see cref="SetAnswer"/>. No interface seam or extension axis is added.
/// </para>
/// </remarks>
public sealed class Questionnaire
{
    private readonly QuestionnaireStep[] _steps;
    private readonly Dictionary<string, QuestionnaireAnswer> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _externalErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _validated = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private readonly GenericTextField _input = new() { Placeholder = "type an answer" };
    private int _current;
    private int _focus;
    private bool _complete;

    /// <summary>
    /// Builds a questionnaire from host steps. Duplicate ids are a schema
    /// error: answers and errors are keyed by id.
    /// </summary>
    public Questionnaire(IEnumerable<QuestionnaireStep> steps)
    {
        Ensure.NotNull(steps);
        _steps = steps.ToArray();
        if (_steps.Length == 0)
        {
            throw new ArgumentException("A questionnaire needs at least one step.", nameof(steps));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in _steps)
        {
            Ensure.NotNull(step);
            if (!ids.Add(step.Id))
            {
                throw new ArgumentException($"Duplicate questionnaire step id: {step.Id}.", nameof(steps));
            }
        }

        SyncInput();
    }

    /// <summary>Step definitions in host order.</summary>
    public IReadOnlyList<QuestionnaireStep> Steps => _steps;

    /// <summary>Index of the active step.</summary>
    public int CurrentIndex => _current;

    /// <summary>Active step definition, or null when the flow is complete.</summary>
    public QuestionnaireStep? CurrentStep => _complete ? null : _steps[_current];

    /// <summary>Total number of steps.</summary>
    public int Total => _steps.Length;

    /// <summary>True after a successful submit.</summary>
    public bool IsComplete => _complete;

    /// <summary>True while a previous step exists.</summary>
    public bool CanGoBack => !_complete && _current > 0;

    /// <summary>True while a next step exists.</summary>
    public bool CanGoNext => !_complete && _current < _steps.Length - 1;

    /// <summary>Focused choice index, or the input row index when freeform is present.</summary>
    public int FocusedIndex => _focus;

    /// <summary>Active validation error of the current step, if any.</summary>
    public string? Error => CurrentStep is { } step ? ErrorAt(step) : null;

    /// <summary>Draft snapshot keyed by step id, in schema order.</summary>
    public IReadOnlyDictionary<string, QuestionnaireAnswer> Answers()
    {
        var snapshot = new Dictionary<string, QuestionnaireAnswer>(_steps.Length, StringComparer.Ordinal);
        foreach (var step in _steps)
        {
            snapshot[step.Id] = _answers.TryGetValue(step.Id, out var answer) ? answer : default;
        }

        return snapshot;
    }

    /// <summary>Draft answer of one step; empty when it has not been answered.</summary>
    public QuestionnaireAnswer Answer(string id) =>
        _answers.TryGetValue(id, out var answer) ? answer : default;

    /// <summary>True when the host skipped this step.</summary>
    public bool IsSkipped(string id) => _skipped.Contains(id);

    /// <summary>
    /// Validation error of one step, with a host error taking precedence over
    /// the step's own rule. A rule is only reported after validation was tried.
    /// </summary>
    public string? ErrorAt(string id)
    {
        foreach (var step in _steps)
        {
            if (string.Equals(step.Id, id, StringComparison.Ordinal))
            {
                return ErrorAt(step);
            }
        }

        return null;
    }

    /// <summary>Stores a draft answer. Changing an answer reopens a completed flow.</summary>
    public void SetAnswer(string id, QuestionnaireAnswer answer)
    {
        var step = Find(id);
        if (step is null)
        {
            return;
        }

        var normalized = new QuestionnaireAnswer(answer.Choice, answer.Text ?? string.Empty);
        _answers[id] = normalized;
        _skipped.Remove(id);
        _complete = false;
        if (step == CurrentStep)
        {
            SyncInput();
        }
    }

    /// <summary>Sets a host validation error for a step; null clears it.</summary>
    public void SetExternalError(string id, string? error)
    {
        if (Find(id) is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(error))
        {
            _ = _externalErrors.Remove(id);
        }
        else
        {
            _externalErrors[id] = error;
        }
    }

    /// <summary>Painted row count for the current step at its current error state.</summary>
    public int DesiredHeight
    {
        get
        {
            var step = CurrentStep;
            if (step is null)
            {
                return 1;
            }

            int rows = 3 + step.Values.Count + (step.Freeform ? 1 : 0);
            if (step.Description is { Length: > 0 })
            {
                rows++;
            }

            return ErrorAt(step) is null ? rows : rows + 1;
        }
    }

    /// <summary>
    /// Validates the current step and moves forward. A failed validation leaves
    /// the step active and publishes the error. Returns true when moved.
    /// </summary>
    public bool GoNext()
    {
        var step = CurrentStep;
        if (step is null || _current >= _steps.Length - 1)
        {
            return false;
        }

        _ = _validated.Add(step.Id);
        if (ErrorAt(step) is not null)
        {
            return false;
        }

        _current++;
        ResetFocus();
        return true;
    }

    /// <summary>Moves back without validating. The answer draft is kept.</summary>
    public bool GoBack()
    {
        if (!CanGoBack)
        {
            return false;
        }

        _complete = false;
        _current--;
        ResetFocus();
        return true;
    }

    /// <summary>
    /// Skips an optional step: clears its answer, records the skip and moves
    /// on. Required steps cannot be skipped.
    /// </summary>
    public bool Skip()
    {
        var step = CurrentStep;
        if (step is null || step.Required)
        {
            return false;
        }

        _answers.Remove(step.Id);
        _ = _externalErrors.Remove(step.Id);
        _ = _validated.Remove(step.Id);
        _ = _skipped.Add(step.Id);
        if (_current == _steps.Length - 1)
        {
            return Submit();
        }

        _current++;
        ResetFocus();
        return true;
    }

    /// <summary>
    /// Validates every step and completes the flow. On failure the first
    /// invalid step becomes active; on success <see cref="IsComplete"/> is true.
    /// </summary>
    public bool Submit()
    {
        foreach (var step in _steps)
        {
            _ = _validated.Add(step.Id);
        }

        foreach (var step in _steps)
        {
            if (ErrorAt(step) is not null)
            {
                int index = Array.IndexOf(_steps, step);
                if (index != _current)
                {
                    _current = index;
                    ResetFocus();
                }

                return false;
            }
        }

        _complete = true;
        return true;
    }

    /// <summary>Clears all answers, errors, skips and completion state.</summary>
    public void Reset()
    {
        _answers.Clear();
        _externalErrors.Clear();
        _validated.Clear();
        _skipped.Clear();
        _current = 0;
        _complete = false;
        ResetFocus();
    }

    /// <summary>
    /// Routes keys: Up/Down move the focused row, Tab cycles it, a space toggles
    /// the focused choice, Enter confirms the step, Escape goes back, and text
    /// keys go to the freeform field when it is focused.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (key.EventType == KeyEventType.Release || CurrentStep is not { } step)
        {
            return false;
        }

        int rows = step.Values.Count + (step.Freeform ? 1 : 0);
        switch (key.Key)
        {
            case KeyCode.Up:
                _focus = Math.Max(0, _focus - 1);
                return rows > 0;
            case KeyCode.Down:
                _focus = Math.Min(Math.Max(0, rows - 1), _focus + 1);
                return rows > 0;
            case KeyCode.Tab when rows > 0:
                _focus = (_focus + 1) % rows;
                return true;
            case KeyCode.Escape:
                return GoBack();
            case KeyCode.Enter:
                return Confirm();
            case KeyCode.Char when key.Character == new Rune(' ') && _focus < step.Values.Count:
                ToggleChoice(step, step.Values[_focus]);
                return true;
        }

        if (step.Freeform && _focus == step.Values.Count)
        {
            _input.IsFocused = true;
            if (_input.HandleKey(in key))
            {
                StoreText(step);
                return true;
            }
        }

        return false;
    }

    /// <summary>Confirms the current step: next, or submit on the last one.</summary>
    public bool Confirm()
    {
        var step = CurrentStep;
        if (step is null)
        {
            return false;
        }

        if (_focus < step.Values.Count && step.Values.Count > 0 && Answer(step.Id).IsEmpty)
        {
            _answers[step.Id] = new QuestionnaireAnswer(step.Values[_focus], Answer(step.Id).Text);
            _ = _skipped.Remove(step.Id);
        }

        if (_current == _steps.Length - 1)
        {
            return Submit();
        }

        return GoNext();
    }

    /// <summary>Paints the active step into <paramref name="rect"/>.</summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        Ensure.NotNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        if (CurrentStep is not { } step)
        {
            buffer.SetText(rect.X, rect.Y, Fit($"Done: {_steps.Length}/{_steps.Length}", rect.Width), new CellStyle(ChatPalette.Accent));
            return;
        }

        int y = rect.Y;
        var muted = new CellStyle(ChatPalette.Muted);
        var accent = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var text = new CellStyle(ChatPalette.Text);
        var errorStyle = new CellStyle(ChatPalette.Error);

        buffer.SetText(rect.X, y, Fit($"Step {_current + 1}/{_steps.Length}  {step.Title}", rect.Width), accent);
        y++;
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        GenericProgressBar.Paint(
            buffer,
            new Rect(rect.X, y, rect.Width, 1),
            new GenericProgressState((_current + 1.0) / _steps.Length, ShowLabel: false));
        y++;
        if (y >= rect.Y + rect.Height)
        {
            return;
        }

        if (step.Description is { Length: > 0 })
        {
            buffer.SetText(rect.X, y, Fit("  " + step.Description, rect.Width), muted);
            y++;
            if (y >= rect.Y + rect.Height)
            {
                return;
            }
        }

        var answer = Answer(step.Id);
        for (int i = 0; i < step.Values.Count && y < rect.Y + rect.Height; i++, y++)
        {
            bool selected = string.Equals(answer.Choice, step.Values[i], StringComparison.Ordinal);
            string marker = i == _focus ? "> " : "  ";
            buffer.SetText(rect.X, y, Fit(marker + (selected ? "(x) " : "( ) ") + step.Values[i], rect.Width), selected ? text : muted);
        }

        if (step.Freeform && y < rect.Y + rect.Height)
        {
            bool focused = _focus == step.Values.Count;
            buffer.SetText(rect.X, y, focused ? "> " : "  ", focused ? accent : muted);
            var fieldRect = new Rect(rect.X + 2, y, Math.Max(0, rect.Width - 2), 1);
            SyncInput();
            _input.IsFocused = focused;
            _input.Paint(buffer, fieldRect);
            y++;
        }

        if (ErrorAt(step) is { } error && y < rect.Y + rect.Height)
        {
            buffer.SetText(rect.X, y, Fit("  ! " + error, rect.Width), errorStyle);
            y++;
        }

        if (y < rect.Y + rect.Height)
        {
            string hint = CanGoNext ? "  [Enter] Next  [Esc] Back" : "  [Enter] Submit  [Esc] Back";
            buffer.SetText(rect.X, y, Fit(hint, rect.Width), muted);
        }
    }

    private QuestionnaireStep? Find(string id)
    {
        foreach (var step in _steps)
        {
            if (string.Equals(step.Id, id, StringComparison.Ordinal))
            {
                return step;
            }
        }

        return null;
    }

    private string? ErrorAt(QuestionnaireStep step)
    {
        if (_skipped.Contains(step.Id) && !_validated.Contains(step.Id))
        {
            return null;
        }

        if (_externalErrors.TryGetValue(step.Id, out var external))
        {
            return external;
        }

        if (!_validated.Contains(step.Id))
        {
            return null;
        }

        var answer = Answer(step.Id);
        if (answer.IsEmpty)
        {
            return step.Required ? "Required." : null;
        }

        string? message = step.Validate?.Invoke(answer, Answers());
        return string.IsNullOrEmpty(message) ? null : message;
    }

    private void ToggleChoice(QuestionnaireStep step, string value)
    {
        var answer = Answer(step.Id);
        string? choice = string.Equals(answer.Choice, value, StringComparison.Ordinal) ? null : value;
        _answers[step.Id] = new QuestionnaireAnswer(choice, answer.Text);
        _ = _skipped.Remove(step.Id);
        _complete = false;
    }

    private void StoreText(QuestionnaireStep step)
    {
        var answer = Answer(step.Id);
        _answers[step.Id] = new QuestionnaireAnswer(answer.Choice, _input.Text);
        _ = _skipped.Remove(step.Id);
        _complete = false;
    }

    private void SyncInput()
    {
        var step = CurrentStep;
        _input.SetText(step is null ? string.Empty : Answer(step.Id).Text);
    }

    private void ResetFocus()
    {
        _focus = 0;
        SyncInput();
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..width];
}
