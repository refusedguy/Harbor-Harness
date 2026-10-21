using System.Buffers;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Interaction;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The model-facing question channel (issue #1759): the model asks the
///     human a structured question (one or more, each with its own id and an
///     optional list of choices) and the answer comes back as the tool result.
/// </summary>
/// <remarks>
///     <para>
///         This is NOT the permission channel. A permission prompt asks "may I
///         run this?" and is answered allow/deny; this asks "which of these?"
///         and is answered with a choice or with free text. The two never share
///         a shape, and asking a question never grants a permission.
///     </para>
///     <para>
///         <b>The wait is bounded.</b> The model may ask for a window
///         (<c>timeout_ms</c>); when it closes with no answer the tool says so
///         (<c>ASK_PENDING</c>) and the turn continues. A question that
///         waited forever would be a run that never ends.
///     </para>
///     <para>
///         <b>Every outcome is explicit.</b> Answered, timed out, dismissed by
///         the human, or "there is nobody here to ask". Each is a distinct,
///         coded result the model can act on, and none of them is silence.
///     </para>
/// </remarks>
[ToolInfo(
    "ask_user_question",
    "Ask User Question",
    "Ask the user a structured question and wait for the answer. Use it when only the user can decide: which approach, which library, test or documentation. Not a permission request.",
    Glyph = "❓",
    ExecutionMode = ExecutionMode.Sequential,
    PromptSnippet = "ask_user_question: ask the user a structured question (options or free text) and wait for the answer",
    PromptGuidelines = new[]
    {
        "ask only when the answer changes what you do next; a permission prompt is not a question",
        "give every question a stable id and 2-4 concrete options; free text only when no list fits",
        "batch related questions into one call instead of asking one at a time",
        "the wait is bounded: a timeout comes back as ASK_PENDING, never as silence"
    })]
public sealed partial class AskUserQuestionTool : ITool
{
    /// <summary>How many questions one call may carry.</summary>
    public const int MaxQuestions = 5;

    /// <summary>How many options one question may carry.</summary>
    public const int MaxOptions = 10;

    /// <summary>The longest window a call may ask for, in milliseconds.</summary>
    public const int MaxTimeoutMs = 600_000;

    private readonly IUserQuestionService _questions;
    private readonly ILogger<AskUserQuestionTool> _logger;

    /// <summary>
    ///     Construct the tool over the question channel.
    /// </summary>
    /// <param name="questions">The channel the question goes out on.</param>
    /// <param name="logger">The logger.</param>
    public AskUserQuestionTool(IUserQuestionService questions, ILogger<AskUserQuestionTool> logger)
    {
        _questions = questions;
        _logger = logger;
    }

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    /// <summary>
    ///     The tool's parameter schema. Parsed once per process (#1574): the JSON
    ///     below is a compile-time constant, so re-parsing it for every tool
    ///     instance was pure allocation churn.
    /// </summary>
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "questions": {
                                                                            "type": "array",
                                                                            "description": "The questions to ask, in the order the user should see them.",
                                                                            "minItems": 1,
                                                                            "maxItems": 5,
                                                                            "items": {
                                                                              "type": "object",
                                                                              "properties": {
                                                                                "id": {
                                                                                  "type": "string",
                                                                                  "description": "Stable id of the question; echoed back in the answer."
                                                                                },
                                                                                "question": {
                                                                                  "type": "string",
                                                                                  "description": "The question to ask, in your own words."
                                                                                },
                                                                                "header": {
                                                                                  "type": "string",
                                                                                  "description": "Short label for the question."
                                                                                },
                                                                                "options": {
                                                                                  "type": "array",
                                                                                  "description": "The choices the user may pick. Omit for a free-text answer.",
                                                                                  "maxItems": 10,
                                                                                  "items": {
                                                                                    "type": "object",
                                                                                    "properties": {
                                                                                      "label": {
                                                                                        "type": "string",
                                                                                        "description": "The option text; echoed back when picked."
                                                                                      },
                                                                                      "description": {
                                                                                        "type": "string",
                                                                                        "description": "What picking this option means."
                                                                                      }
                                                                                    },
                                                                                    "required": ["label"]
                                                                                  }
                                                                                },
                                                                                "multi_select": {
                                                                                  "type": "boolean",
                                                                                  "description": "Whether the user may pick more than one option (default false)."
                                                                                }
                                                                              },
                                                                              "required": ["id", "question"]
                                                                            }
                                                                          },
                                                                          "timeout_ms": {
                                                                            "type": "integer",
                                                                            "description": "How long to wait for the answer, in milliseconds (default 120000, max 600000)."
                                                                          }
                                                                        },
                                                                        "required": ["questions"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public JsonDocument ParameterSchema => Schema;

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        Result<IReadOnlyList<UserQuestion>> parsed = ParseQuestions(args);
        if (parsed.IsFailure)
        {
            return Result.Failure(parsed.Error);
        }

        return JsonArgValidator.OptionalIntInRange(
            args,
            "timeout_ms",
            1,
            MaxTimeoutMs,
            $"Optional argument 'timeout_ms' must be an integer between 1 and {MaxTimeoutMs}.");
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Result<IReadOnlyList<UserQuestion>> parsed = ParseQuestions(args);
        if (parsed.IsFailure)
        {
            return ToolResult.Error(parsed.Error);
        }

        IReadOnlyList<UserQuestion> questions = parsed.Value;

        TimeSpan? window = null;
        if (args.TryGetProperty("timeout_ms", out var timeoutEl)
            && timeoutEl.ValueKind == JsonValueKind.Number
            && timeoutEl.TryGetInt32(out int timeoutMs))
        {
            window = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, MaxTimeoutMs));
        }

        var request = new UserQuestionRequest(questions, window);
        UserQuestionOutcome outcome = await _questions.AskAsync(request, cancellationToken).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case UserQuestionStatus.Answered:
                AskUserQuestionToolLog.Answered(_logger, questions.Count, outcome.Answers.Count);
                return ToolResult.Success(RenderAnswers(outcome.Answers));

            case UserQuestionStatus.Pending:
                AskUserQuestionToolLog.Pending(_logger, questions.Count, window?.TotalSeconds ?? 0);
                return ToolResult.Error(Describe(outcome, UserQuestionCodes.Pending));

            case UserQuestionStatus.Aborted:
                AskUserQuestionToolLog.Aborted(_logger, questions.Count);
                return ToolResult.Error(Describe(outcome, UserQuestionCodes.Aborted));

            default:
                AskUserQuestionToolLog.Unavailable(_logger, questions.Count);
                return ToolResult.Error(Describe(outcome, UserQuestionCodes.NoChannel));
        }
    }

    /// <summary>
    ///     The model-facing text of a non-answer: the stable code first, then
    ///     the reason. The code is the contract; the sentence around it may be
    ///     reworded.
    /// </summary>
    /// <param name="outcome">The outcome the channel returned.</param>
    /// <param name="fallbackCode">The code to use when the outcome carries none.</param>
    /// <returns>The text the model reads back.</returns>
    private static string Describe(UserQuestionOutcome outcome, string fallbackCode)
    {
        string code = outcome.Code ?? fallbackCode;
        string reason = outcome.Reason ?? "the question was not answered.";
        return $"{code}: {reason}";
    }

    /// <summary>
    ///     The answer payload the model reads back: one entry per answered
    ///     question, carrying the question's own id.
    /// </summary>
    /// <param name="answers">The answers.</param>
    /// <returns>A JSON document.</returns>
    /// <remarks>
    ///     Written with <see cref="Utf8JsonWriter" /> rather than
    ///     <c>JsonSerializer</c>: the payload is written on the tool path, and
    ///     the reflection-based serializer is both an AOT hazard and an
    ///     allocation the BCL writer does not have.
    /// </remarks>
    private static string RenderAnswers(IReadOnlyList<UserQuestionAnswer> answers)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", UserQuestionCodes.Answered);
            writer.WriteStartArray("answers");
            for (int i = 0; i < answers.Count; i++)
            {
                UserQuestionAnswer answer = answers[i];
                writer.WriteStartObject();
                writer.WriteString("id", answer.Id);
                writer.WriteStartArray("selected");
                for (int j = 0; j < answer.Selected.Count; j++)
                {
                    writer.WriteStringValue(answer.Selected[j]);
                }

                writer.WriteEndArray();
                if (answer.Custom is not null)
                {
                    writer.WriteString("custom", answer.Custom);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Read the questions out of the raw arguments. The same read serves
    ///     validation and execution, so the two can never disagree about what
    ///     the call asked.
    /// </summary>
    /// <param name="args">The raw JSON arguments.</param>
    /// <returns>The questions, or the reason the arguments are unusable.</returns>
    private static Result<IReadOnlyList<UserQuestion>> ParseQuestions(JsonElement args)
    {
        if (!args.TryGetProperty("questions", out var questionsEl)
            || questionsEl.ValueKind != JsonValueKind.Array)
        {
            return Result.Failure<IReadOnlyList<UserQuestion>>(
                "Missing required argument 'questions', an array of questions to ask.");
        }

        int count = questionsEl.GetArrayLength();
        if (count == 0)
        {
            return Result.Failure<IReadOnlyList<UserQuestion>>(
                "Argument 'questions' must contain at least one question.");
        }

        if (count > MaxQuestions)
        {
            return Result.Failure<IReadOnlyList<UserQuestion>>(
                $"Argument 'questions' accepts at most {MaxQuestions} questions (got {count}).");
        }

        var questions = new List<UserQuestion>(count);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement question in questionsEl.EnumerateArray())
        {
            if (question.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<IReadOnlyList<UserQuestion>>(
                    "Every entry of 'questions' must be an object with 'id' and 'question'.");
            }

            if (!question.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idEl.GetString()))
            {
                return Result.Failure<IReadOnlyList<UserQuestion>>(
                    "Every question needs a non-empty string 'id'.");
            }

            string id = idEl.GetString()!;
            if (!ids.Add(id))
            {
                return Result.Failure<IReadOnlyList<UserQuestion>>(
                    $"Two questions share the id '{id}': ids must be unique within one call.");
            }

            if (!question.TryGetProperty("question", out var textEl)
                || textEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(textEl.GetString()))
            {
                return Result.Failure<IReadOnlyList<UserQuestion>>(
                    $"Question '{id}' needs a non-empty string 'question'.");
            }

            string text = textEl.GetString()!;

            string? header = null;
            if (question.TryGetProperty("header", out var headerEl)
                && headerEl.ValueKind == JsonValueKind.String)
            {
                header = headerEl.GetString();
            }

            Result<IReadOnlyList<UserQuestionOption>> options = ParseOptions(question, id);
            if (options.IsFailure)
            {
                return Result.Failure<IReadOnlyList<UserQuestion>>(options.Error);
            }

            bool multiSelect = false;
            if (question.TryGetProperty("multi_select", out var multiEl)
                && (multiEl.ValueKind == JsonValueKind.True || multiEl.ValueKind == JsonValueKind.False))
            {
                multiSelect = multiEl.GetBoolean();
            }

            questions.Add(new UserQuestion(id, text, header, options.Value, multiSelect));
        }

        return Result.Success<IReadOnlyList<UserQuestion>>(questions);
    }

    /// <summary>
    ///     Read one question's options.
    /// </summary>
    /// <param name="question">The question object.</param>
    /// <param name="id">The question's id, for the error text.</param>
    /// <returns>The options, or the reason they are unusable.</returns>
    private static Result<IReadOnlyList<UserQuestionOption>> ParseOptions(JsonElement question, string id)
    {
        if (!question.TryGetProperty("options", out var optionsEl)
            || optionsEl.ValueKind != JsonValueKind.Array)
        {
            // No options is a free-text question, not an error.
            return Result.Success<IReadOnlyList<UserQuestionOption>>(Array.Empty<UserQuestionOption>());
        }

        int count = optionsEl.GetArrayLength();
        if (count > MaxOptions)
        {
            return Result.Failure<IReadOnlyList<UserQuestionOption>>(
                $"Question '{id}' carries {count} options; at most {MaxOptions} are allowed.");
        }

        var options = new List<UserQuestionOption>(count);
        var labels = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement option in optionsEl.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<IReadOnlyList<UserQuestionOption>>(
                    $"Every option of question '{id}' must be an object with a 'label'.");
            }

            if (!option.TryGetProperty("label", out var labelEl)
                || labelEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(labelEl.GetString()))
            {
                return Result.Failure<IReadOnlyList<UserQuestionOption>>(
                    $"Every option of question '{id}' needs a non-empty string 'label'.");
            }

            string label = labelEl.GetString()!;
            if (!labels.Add(label))
            {
                return Result.Failure<IReadOnlyList<UserQuestionOption>>(
                    $"Question '{id}' has two options labelled '{label}': labels must be unique.");
            }

            string? description = null;
            if (option.TryGetProperty("description", out var descriptionEl)
                && descriptionEl.ValueKind == JsonValueKind.String)
            {
                description = descriptionEl.GetString();
            }

            options.Add(new UserQuestionOption(label, description));
        }

        return Result.Success<IReadOnlyList<UserQuestionOption>>(options);
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for <see cref="AskUserQuestionTool" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class AskUserQuestionToolLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ask_user_question: {QuestionCount} question(s) answered ({AnswerCount} answer(s))")]
    public static partial void Answered(ILogger logger, int questionCount, int answerCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ask_user_question: {QuestionCount} question(s) left unanswered after the wait window ({WindowSeconds:0}s)")]
    public static partial void Pending(ILogger logger, int questionCount, double windowSeconds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "ask_user_question: {QuestionCount} question(s) dismissed by the user")]
    public static partial void Aborted(ILogger logger, int questionCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "ask_user_question: {QuestionCount} question(s) could not be asked, no interactive user")]
    public static partial void Unavailable(ILogger logger, int questionCount);
}
