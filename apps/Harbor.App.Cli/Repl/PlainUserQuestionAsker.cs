using CSharpFunctionalExtensions;
using Harbor.Abstractions.Interaction;
using Harbor.Terminal.Abstractions;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     The console question surface (issue #1759): it prints the question,
///     lists the options, and reads the answer as one line.
/// </summary>
/// <remarks>
///     <para>
///         This is the counterpart of <see cref="PlainPermissionAsker" /> for
///         the question channel, and it exists for the same reason: the
///         fullscreen renderer answers a permission on a card inside its frame
///         loop, and a renderer without that loop has nothing but the
///         console. The question is printed, the answer is typed, and the
///         answer goes back to the model as the tool result.
///     </para>
///     <para>
///         The answer line is read the way a person answers a question at a
///         counter: a number picks an option, the option's own text picks it
///         too, and anything else is the person's own answer. A leading
///         <c>!</c> skips the rest of the batch, which is how a person says
///         "not now" without closing the session.
///     </para>
/// </remarks>
internal sealed class PlainUserQuestionAsker(ITuiRenderer renderer)
{
    /// <summary>The line that skips the rest of the batch.</summary>
    private const string SkipMarker = "!";

    /// <summary>
    ///     Ask the questions one after another and collect the answers.
    /// </summary>
    /// <param name="request">The questions and the wait window.</param>
    /// <param name="ct">The caller's token; a cancelled caller never gets a verdict.</param>
    /// <returns>The outcome: answered, or aborted when the person skipped it or the input ended.</returns>
    public async Task<UserQuestionOutcome> AskAsync(UserQuestionRequest request, CancellationToken ct)
    {
        var answers = new List<UserQuestionAnswer>(request.Questions.Count);
        int total = request.Questions.Count;

        for (int i = 0; i < total; i++)
        {
            UserQuestion question = request.Questions[i];
            await WriteQuestionAsync(question, i + 1, total, ct).ConfigureAwait(false);

            Maybe<string> line = await renderer
                .ReadLineAsync(Prompt(question, i + 1, total), ct)
                .ConfigureAwait(false);

            if (!line.HasValue)
            {
                return UserQuestionOutcome.Aborted(
                    "The input ended before the question was answered.");
            }

            string reply = line.GetValueOrDefault(string.Empty).Trim();
            if (reply.StartsWith(SkipMarker, StringComparison.Ordinal))
            {
                return UserQuestionOutcome.Aborted(
                    "The user skipped the question without answering it.");
            }

            UserQuestionAnswer? answer = ParseAnswer(question, reply);
            if (answer is not null)
            {
                answers.Add(answer);
            }
        }

        return UserQuestionOutcome.Answered(answers);
    }

    /// <summary>
    ///     Print one question and its options.
    /// </summary>
    /// <param name="question">The question to print.</param>
    /// <param name="index">Its 1-based position in the batch.</param>
    /// <param name="total">How many questions the batch carries.</param>
    /// <param name="ct">The caller's token.</param>
    /// <returns>A task that completes when the question is on the screen.</returns>
    private async Task WriteQuestionAsync(
        UserQuestion question,
        int index,
        int total,
        CancellationToken ct)
    {
        string header = question.Header is { Length: > 0 } ? question.Header : $"question {index}/{total}";
        await renderer.WriteLineAsync($"[{header}] {question.Question}", ct).ConfigureAwait(false);

        IReadOnlyList<UserQuestionOption> options = question.Options ?? Array.Empty<UserQuestionOption>();
        for (int i = 0; i < options.Count; i++)
        {
            string description = options[i].Description is { Length: > 0 } d ? $": {d}" : string.Empty;
            await renderer
                .WriteLineAsync($"  {i + 1}) {options[i].Label}{description}", ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The prompt that asks for one answer.
    /// </summary>
    /// <param name="question">The question being answered.</param>
    /// <param name="index">Its 1-based position in the batch.</param>
    /// <param name="total">How many questions the batch carries.</param>
    /// <returns>The prompt text.</returns>
    private static string Prompt(UserQuestion question, int index, int total)
    {
        bool hasOptions = question.Options is { Count: > 0 };
        string shape = hasOptions
            ? $"type the number of an option{(question.MultiSelect ? " (comma-separated for several)" : string.Empty)}, or your own answer"
            : "type your answer";
        return $"answer {index}/{total} ({shape}, '{SkipMarker}' to skip): ";
    }

    /// <summary>
    ///     Read one answer line into an answer.
    /// </summary>
    /// <param name="question">The question being answered.</param>
    /// <param name="reply">The line the person typed.</param>
    /// <returns>The answer, or null when the line was empty (the question stays unanswered).</returns>
    private static UserQuestionAnswer? ParseAnswer(UserQuestion question, string reply)
    {
        if (reply.Length == 0)
        {
            return null;
        }

        IReadOnlyList<UserQuestionOption> options = question.Options ?? Array.Empty<UserQuestionOption>();
        if (options.Count == 0)
        {
            // A free-text question: the whole line is the answer.
            return new UserQuestionAnswer(question.Id, Array.Empty<string>(), reply);
        }

        var selected = new List<string>();
        string? custom = null;
        string[] parts = reply.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            UserQuestionOption? option = MatchOption(options, part);
            if (option is not null)
            {
                if (!selected.Contains(option.Label, StringComparer.Ordinal))
                {
                    selected.Add(option.Label);
                }

                continue;
            }

            // Not an option: the person's own words. They replace the choice.
            custom = custom is null ? part : custom + ", " + part;
        }

        if (selected.Count == 0 && custom is null)
        {
            return null;
        }

        return new UserQuestionAnswer(question.Id, selected, custom);
    }

    /// <summary>
    ///     Match one typed fragment against the options: by 1-based number, or
    ///     by the option's own label.
    /// </summary>
    /// <param name="options">The question's options.</param>
    /// <param name="fragment">The typed fragment.</param>
    /// <returns>The option it names, or null.</returns>
    private static UserQuestionOption? MatchOption(IReadOnlyList<UserQuestionOption> options, string fragment)
    {
        if (int.TryParse(fragment, out int number)
            && number >= 1
            && number <= options.Count)
        {
            return options[number - 1];
        }

        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Label, fragment, StringComparison.OrdinalIgnoreCase))
            {
                return options[i];
            }
        }

        return null;
    }
}
