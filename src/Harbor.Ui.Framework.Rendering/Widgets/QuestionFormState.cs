using System.Collections.Immutable;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
///     Interactive state of one question of a <see cref="QuestionFormView" />:
///     cursor row, selected option indices and the free-text custom answer.
///     Immutable value; the widget used to keep a mutable HashSet and
///     StringBuilder pair here (issue #2627).
/// </summary>
public readonly record struct QuestionAnswerState(
    int Cursor,
    ImmutableArray<int> Selected,
    string Custom)
{
    public static readonly QuestionAnswerState Empty = new(0, ImmutableArray<int>.Empty, string.Empty);
}

/// <summary>
///     Immutable answer state of a question form. This is the slice of
///     UiState.Chat that QuestionFormView projects; the widget never owns a
///     mutable answer buffer. Every transition returns a new snapshot (or
///     <c>this</c> for a no-op), so one state always paints the same card and
///     a replay of the store reproduces the picture exactly (#2627, FP-010).
/// </summary>
public sealed record QuestionFormState
{
    /// <summary>Per-question interactive state, in host order.</summary>
    public ImmutableArray<QuestionAnswerState> Questions { get; init; } = ImmutableArray<QuestionAnswerState>.Empty;

    /// <summary>Submitted answers (selected labels + custom text), recorded once.</summary>
    public ImmutableArray<QuestionAnswer> SubmittedAnswers { get; init; } = ImmutableArray<QuestionAnswer>.Empty;

    /// <summary>True after submit; the answers are frozen from then on.</summary>
    public bool IsSubmitted { get; init; }

    /// <summary>Fresh, pending state for <paramref name="count" /> questions.</summary>
    public static QuestionFormState Create(int count)
    {
        var builder = ImmutableArray.CreateBuilder<QuestionAnswerState>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            builder.Add(QuestionAnswerState.Empty);
        }

        return new QuestionFormState { Questions = builder.ToImmutable() };
    }

    /// <summary>Empty form (no questions, not submitted).</summary>
    public static readonly QuestionFormState Empty = new();

    private static int RowCount(QuestionItem item) => item.Options.Count + (item.AllowCustom ? 1 : 0);

    private QuestionFormState Replace(int index, QuestionAnswerState state)
    {
        var builder = Questions.ToBuilder();
        builder[index] = state;
        return this with { Questions = builder.ToImmutable() };
    }

    /// <summary>Move the option cursor of one question by a signed delta (clamped).</summary>
    public QuestionFormState MoveCursor(int index, int delta, QuestionItem item)
    {
        if (IsSubmitted || delta == 0 || (uint)index >= (uint)Questions.Length)
        {
            return this;
        }

        int rows = RowCount(item);
        if (rows == 0)
        {
            return this;
        }

        var current = Questions[index];
        int next = Math.Clamp(current.Cursor, 0, rows - 1) + delta;
        next = Math.Clamp(next, 0, rows - 1);
        return next == current.Cursor ? this : Replace(index, current with { Cursor = next });
    }

    /// <summary>Toggle/select the option under a question cursor (pure).</summary>
    public QuestionFormState ToggleAtCursor(int index, QuestionItem item)
    {
        if (IsSubmitted || (uint)index >= (uint)Questions.Length)
        {
            return this;
        }

        var current = Questions[index];
        int cursor = Math.Clamp(current.Cursor, 0, Math.Max(0, RowCount(item) - 1));
        if (cursor >= item.Options.Count)
        {
            return this; // custom row — no toggle semantic
        }

        if (item.MultiSelect)
        {
            var set = current.Selected;
            int at = set.IndexOf(cursor);
            return Replace(index, current with { Selected = at >= 0 ? set.RemoveAt(at) : set.Add(cursor) });
        }

        if (current.Selected.Length == 1 && current.Selected[0] == cursor)
        {
            return Replace(index, current with { Selected = ImmutableArray<int>.Empty });
        }

        return Replace(index, current with { Selected = ImmutableArray.Create(cursor) });
    }

    /// <summary>Replace a question's custom answer (prefill path).</summary>
    public QuestionFormState SetCustom(int index, string? text)
    {
        if (IsSubmitted || (uint)index >= (uint)Questions.Length)
        {
            return this;
        }

        string value = text ?? string.Empty;
        var current = Questions[index];
        if (string.Equals(current.Custom, value, StringComparison.Ordinal))
        {
            return this;
        }

        return Replace(index, current with { Custom = value });
    }

    /// <summary>Append text to a question's custom answer.</summary>
    public QuestionFormState AppendCustom(int index, string text)
    {
        if (IsSubmitted || (uint)index >= (uint)Questions.Length || text.Length == 0)
        {
            return this;
        }

        var current = Questions[index];
        return Replace(index, current with { Custom = current.Custom + text });
    }

    /// <summary>Delete one rune (a surrogate pair counts as one) from a custom answer.</summary>
    public QuestionFormState BackspaceCustom(int index)
    {
        if (IsSubmitted || (uint)index >= (uint)Questions.Length)
        {
            return this;
        }

        var current = Questions[index];
        string custom = current.Custom;
        if (custom.Length == 0)
        {
            return this;
        }

        int take = custom.Length >= 2 && char.IsLowSurrogate(custom[^1]) && char.IsHighSurrogate(custom[^2]) ? 2 : 1;
        return Replace(index, current with { Custom = custom[..^take] });
    }

    /// <summary>Freeze the form with its final answers (audit stamp).</summary>
    public QuestionFormState Submit(ImmutableArray<QuestionAnswer> answers) =>
        this with { IsSubmitted = true, SubmittedAnswers = answers };

    /// <summary>Snapshot the visible answers (selected labels + custom text per question).</summary>
    public ImmutableArray<QuestionAnswer> ToAnswers(IReadOnlyList<QuestionItem> questions)
    {
        int count = Math.Min(questions.Count, Questions.Length);
        var builder = ImmutableArray.CreateBuilder<QuestionAnswer>(count);
        for (int i = 0; i < count; i++)
        {
            var question = Questions[i];
            var labels = ImmutableArray.CreateBuilder<string>(question.Selected.Length);
            // SelectedOptions() in the widget sorted ascending — preserve that order.
            int[] selected = question.Selected.ToArray();
            Array.Sort(selected);
            for (int j = 0; j < selected.Length; j++)
            {
                int option = selected[j];
                if ((uint)option < (uint)questions[i].Options.Count)
                {
                    labels.Add(questions[i].Options[option].Label);
                }
            }

            builder.Add(new QuestionAnswer(i, labels.ToImmutable(), question.Custom));
        }

        return builder.ToImmutable();
    }
}
