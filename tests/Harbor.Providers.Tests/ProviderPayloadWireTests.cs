using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Providers.Anthropic;
using Harbor.Providers.Ollama;
using Harbor.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Providers.Tests;

/// <summary>
///     Issue #475 — the provider payload builders write to a
///     <see cref="System.Text.Json.Utf8JsonWriter" /> instead of handing a
///     <c>Dictionary&lt;string, object?&gt;</c> of anonymous types to
///     <c>JsonSerializer.SerializeToUtf8Bytes</c>.
/// </summary>
/// <remarks>
///     <para>
///         Why these tests exist at all, given that <c>ImageContentBlockTests</c>
///         already asserts the image branches: the reflection path carried options
///         whose semantics the writers had to reproduce by hand —
///         <c>JsonSerializerDefaults.Web</c> (camelCase property names) and
///         <c>DefaultIgnoreCondition = WhenWritingNull</c> — and nothing in the
///         suite pinned the second one. A writer that always emits
///         <c>"content": null</c> for a tool-calling turn looks fine in a code
///         review and is a provider 400 at run time.
///     </para>
///     <para>
///         Expectations are byte-exact strings rather than property probes on
///         purpose: the body IS the artifact. A parse-and-probe test passes happily
///         on a payload with a renamed key, a reordered object or a spurious null.
///         These are characterization tests — if a change here is a deliberate wire
///         change, the diff is the review.
///     </para>
/// </remarks>
public class ProviderPayloadWireTests
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"path":{"type":"string"}}}""");

    private static ToolDefinition Tool(string name = "read") =>
        new(name, "Read a file.", Schema);

    private static LlmRequest Request(
        string model = "gpt-4o",
        IReadOnlyList<LlmMessage>? messages = null,
        string system = "be brief",
        IReadOnlyList<ToolDefinition>? tools = null,
        ToolChoice? toolChoice = null,
        int? maxTokens = null,
        decimal? temperature = null,
        decimal? topP = null,
        int? topK = null,
        ReasoningEffort? effort = null,
        CacheStrategy cache = CacheStrategy.None) =>
        new(
            Model: model,
            Messages: messages ?? [],
            SystemPrompt: system,
            Tools: tools ?? [],
            ToolChoice: toolChoice,
            MaxOutputTokens: maxTokens,
            Temperature: temperature,
            TopP: topP,
            TopK: topK,
            ReasoningEffort: effort,
            CacheStrategy: cache);

    private static string Body(HttpRequestMessage message) =>
        message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    private static string OllamaBody(LlmRequest request, OllamaConfig? config = null) =>
        Body(new OllamaLlmClient(
                new HttpClient(),
                config ?? new OllamaConfig(),
                NullLogger<OllamaLlmClient>.Instance)
            .BuildRequest(request));

    // =================================================================
    // Anthropic /messages
    // =================================================================

    [Test]
    public async Task Anthropic_TextOnlyRequest_PinsEveryKey()
    {
        var request = Request(
            model: "claude-opus-4",
            messages: [LlmUserMessage.Text("hi")],
            maxTokens: 4096,
            temperature: 0.7m,
            topP: 0.9m,
            topK: 40);

        string body = Body(AnthropicRequestBuilder.BuildRequest(
            request, "https://api.anthropic.com/v1", "key", null, null));

        await Assert.That(body).IsEqualTo(
            """
            {"model":"claude-opus-4","messages":[{"role":"user","content":"hi"}],"stream":true,"max_tokens":4096,"system":"be brief","temperature":0.7,"top_p":0.9,"top_k":40}""")
            .Because("one typo in a wire key here is a provider 400, and the whole point of the "
                   + "writer is that the key is now written by hand rather than inferred");
    }

    [Test]
    public async Task Anthropic_NoMaxTokens_FallsBackToTheDocumentedDefault()
    {
        string body = Body(AnthropicRequestBuilder.BuildRequest(
            Request(system: ""), "https://api.anthropic.com/v1", "key", null, null));

        await Assert.That(body).IsEqualTo(
            """
            {"model":"gpt-4o","messages":[],"stream":true,"max_tokens":8192}""")
            .Because("8192 is the default max_tokens, and an empty system prompt must not appear "
                   + "as an empty string");
    }

    [Test]
    public async Task Anthropic_EphemeralCache_EmitsTheSystemBlockArray()
    {
        string body = Body(AnthropicRequestBuilder.BuildRequest(
            Request(cache: CacheStrategy.Ephemeral),
            "https://api.anthropic.com/v1", "key", null, null));

        await Assert.That(body).IsEqualTo(
            """
            {"model":"gpt-4o","messages":[],"stream":true,"max_tokens":8192,"system":[{"type":"text","text":"be brief","cache_control":{"type":"ephemeral"}}]}""");
    }

    [Test]
    public async Task Anthropic_ReasoningEffort_EmitsTheThinkingBudgetTable()
    {
        foreach ((ReasoningEffort effort, int budget) in new[]
                 {
                     (ReasoningEffort.Low, 5000),
                     (ReasoningEffort.Medium, 10000),
                     (ReasoningEffort.High, 20000),
                     (ReasoningEffort.Max, 32000),
                 })
        {
            string body = Body(AnthropicRequestBuilder.BuildRequest(
                Request(effort: effort), "https://api.anthropic.com/v1", "key", null, null));

            await Assert.That(body)
                .Contains($"\"thinking\":{{\"type\":\"enabled\",\"budget_tokens\":{budget}}}")
                .Because($"{effort} maps to a {budget}-token budget; the table is the model's, "
                       + "not a ratio of the context window");
        }
    }

    [Test]
    public async Task Anthropic_ContentBlocksAndToolResult_PinTheBlockArray()
    {
        using var arguments = JsonDocument.Parse("""{"path":"a.txt"}""");

        var request = Request(
            model: "claude-opus-4",
            messages:
            [
                new LlmUserMessage([
                    new LlmTextBlock("look"),
                    new LlmImageBlock("image/png", [1, 2, 3]),
                    new LlmThinkingBlock("hmm")
                ]),
                new LlmAssistantMessage([
                    new LlmThinkingBlock("thinking"),
                    new LlmToolCallBlock("call_1", "read", arguments.RootElement)
                ]),
                new LlmToolResultMessage("call_1", "read", "file body", isError: false)
            ]);

        string body = Body(AnthropicRequestBuilder.BuildRequest(
            request, "https://api.anthropic.com/v1", "key", null, null));

        using JsonDocument parsed = JsonDocument.Parse(body);
        JsonElement[] messages = parsed.RootElement.GetProperty("messages");

        JsonElement[] first = [.. messages[0].GetProperty("content").EnumerateArray()];
        await Assert.That(first.Length).IsEqualTo(3)
            .Because("a multi-block user message is an array, never the compact string form");
        await Assert.That(first[0].GetProperty("type").GetString()).IsEqualTo("text");
        await Assert.That(first[1].GetProperty("type").GetString()).IsEqualTo("image");
        await Assert.That(first[1].GetProperty("source").GetProperty("data").GetString())
            .IsEqualTo(Convert.ToBase64String([1, 2, 3]));
        await Assert.That(first[2].GetProperty("type").GetString()).IsEqualTo("thinking");

        JsonElement assistantContent = messages[1].GetProperty("content");
        await Assert.That(assistantContent[0].GetProperty("type").GetString()).IsEqualTo("thinking");
        await Assert.That(assistantContent[1].GetProperty("type").GetString()).IsEqualTo("tool_use");
        await Assert.That(assistantContent[1].GetProperty("input").GetProperty("path").GetString())
            .IsEqualTo("a.txt")
            .Because("a tool_use input is the model's own JSON, written verbatim");

        JsonElement toolResult = messages[2];
        await Assert.That(toolResult.GetProperty("role").GetString()).IsEqualTo("user")
            .Because("Anthropic carries tool_result inside a user message");
        await Assert.That(toolResult.GetProperty("content")[0].GetProperty("type").GetString())
            .IsEqualTo("tool_result");
        await Assert.That(toolResult.GetProperty("content")[0].GetProperty("content").GetString())
            .IsEqualTo("file body");
    }

    [Test]
    public async Task Anthropic_ToolChoice_KeepsItsOwnVocabulary()
    {
        // "required" is OpenAI's word for what Anthropic calls "any".
        string required = Body(AnthropicRequestBuilder.BuildRequest(
            Request(tools: [Tool()], toolChoice: ToolChoice.Required),
            "https://api.anthropic.com/v1", "key", null, null));

        await Assert.That(required).Contains("\"tool_choice\":{\"type\":\"any\"}");

        string pinned = Body(AnthropicRequestBuilder.BuildRequest(
            Request(tools: [Tool()], toolChoice: new ToolChoice.Specific("read")),
            "https://api.anthropic.com/v1", "key", null, null));

        await Assert.That(pinned).Contains("\"tool_choice\":{\"type\":\"tool\",\"name\":\"read\"}")
            .Because("a pinned Anthropic tool is a bare name, not a nested function object");

        string tools = Body(AnthropicRequestBuilder.BuildRequest(
            Request(tools: [Tool()], toolChoice: ToolChoice.Auto),
            "https://api.anthropic.com/v1", "key", null, null));

        await Assert.That(tools).Contains(
            """
            "tools":[{"name":"read","description":"Read a file.","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}]""")
            .Because("Anthropic names the schema input_schema and does not nest it under function");
    }

    // =================================================================
    // OpenAI /chat/completions
    // =================================================================

    [Test]
    public async Task OpenAi_ChatCompletions_TextOnlyRequest_PinsEveryKey()
    {
        var request = Request(
            messages: [LlmUserMessage.Text("hi")],
            maxTokens: 512,
            temperature: 0.5m,
            topP: 0.8m);

        string body = Body(OpenAiRequestBuilder.BuildChatCompletionsRequest(
            request, "https://api.openai.com/v1", NullLogger.Instance));

        await Assert.That(body).IsEqualTo(
            """
            {"model":"gpt-4o","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true},"max_tokens":512,"temperature":0.5,"top_p":0.8}""");
    }

    [Test]
    public async Task OpenAi_ChatCompletions_ReasoningModel_DropsSamplingAndRenamesTheTokenCap()
    {
        var request = Request(
            model: "gpt-5",
            messages: [LlmUserMessage.Text("hi")],
            maxTokens: 512,
            temperature: 0.5m,
            topP: 0.8m);

        string body = Body(OpenAiRequestBuilder.BuildChatCompletionsRequest(
            request, "https://api.openai.com/v1", NullLogger.Instance));

        await Assert.That(body).Contains("\"max_completion_tokens\":512")
            .Because("o-series/gpt-5 reject max_tokens");
        await Assert.That(body).DoesNotContain("\"max_tokens\"");
        await Assert.That(body).DoesNotContain("temperature")
            .Because("reasoning models reject temperature and top_p entirely, so sending them is "
                   + "a 400 — they are omitted rather than sent as a default");
        await Assert.That(body).DoesNotContain("top_p");
    }

    [Test]
    public async Task OpenAi_ChatCompletions_ToolCallingTurn_OmitsContentRatherThanSendingNull()
    {
        using var arguments = JsonDocument.Parse("""{"path":"a.txt"}""");

        var request = Request(messages:
        [
            new LlmAssistantMessage([new LlmToolCallBlock("call_1", "read", arguments.RootElement)]),
            new LlmToolResultMessage("call_1", "read", "body", isError: false)
        ]);

        string body = Body(OpenAiRequestBuilder.BuildChatCompletionsRequest(
            request, "https://api.openai.com/v1", NullLogger.Instance));

        using JsonDocument parsed = JsonDocument.Parse(body);
        JsonElement assistant = parsed.RootElement.GetProperty("messages")[0];

        await Assert.That(assistant.TryGetProperty("content", out _)).IsFalse()
            .Because("the old options carried DefaultIgnoreCondition = WhenWritingNull, so an "
                   + "assistant turn with no text block sent NO content key at all. Sending "
                   + "\"content\": null instead is an OpenAI 400 — this assertion is the reason "
                   + "the omission had to be reproduced by hand instead of left to a serializer.");

        JsonElement call = assistant.GetProperty("tool_calls")[0];
        await Assert.That(call.GetProperty("type").GetString()).IsEqualTo("function");
        await Assert.That(call.GetProperty("function").GetProperty("arguments").GetString())
            .IsEqualTo("""{"path":"a.txt"}""")
            .Because("arguments travel as a STRING on this API, not as nested JSON");

        JsonElement result = parsed.RootElement.GetProperty("messages")[1];
        await Assert.That(result.GetProperty("role").GetString()).IsEqualTo("tool");
        await Assert.That(result.GetProperty("tool_call_id").GetString()).IsEqualTo("call_1");
        await Assert.That(result.GetProperty("content").GetString()).IsEqualTo("body");
    }

    [Test]
    public async Task OpenAi_ChatCompletions_TextlessAssistant_StillEmitsAnEmptyToolCallsArray()
    {
        var request = Request(messages: [new LlmAssistantMessage([new LlmTextBlock("plain")])]);

        string body = Body(OpenAiRequestBuilder.BuildChatCompletionsRequest(
            request, "https://api.openai.com/v1", NullLogger.Instance));

        await Assert.That(body)
            .Contains("""{"role":"assistant","content":"plain","tool_calls":[]}""")
            .Because("the key is always present on this API — the compat adapter emits the same "
                   + "shape, and dropping the empty array would be a silent wire change");
    }

    [Test]
    public async Task OpenAi_ChatCompletions_ToolChoice_AndTools_PinTheirShapes()
    {
        string auto = Body(OpenAiRequestBuilder.BuildChatCompletionsRequest(
            Request(tools: [Tool()], toolChoice: ToolChoice.Auto),
            "https://api.openai.com/v1", NullLogger.Instance));

        await Assert.That(auto).Contains("\"tool_choice\":\"auto\"");

        string pinned = Body(OpenAiRequestBuilder.BuildChatCompletionsRequest(
            Request(tools: [Tool()], toolChoice: new ToolChoice.Specific("read")),
            "https://api.openai.com/v1", NullLogger.Instance));

        await Assert.That(pinned).Contains("""{"type":"function","function":{"name":"read"}}""")
            .Because("Chat Completions nests the pinned function, unlike Anthropic's flat name");

        await Assert.That(auto).Contains(
            """
            "tools":[{"type":"function","function":{"name":"read","description":"Read a file.","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}}]
            """);
    }

    // =================================================================
    // OpenAI /responses
    // =================================================================

    [Test]
    public async Task OpenAi_Responses_PinsInputShapeAndInlineToolFields()
    {
        var request = Request(
            model: "gpt-5",
            messages:
            [
                LlmUserMessage.Text("hi"),
                new LlmAssistantMessage([new LlmTextBlock("sure")]),
                new LlmToolResultMessage("call_1", "read", "body", isError: false)
            ],
            tools: [Tool()],
            maxTokens: 256,
            effort: ReasoningEffort.High);

        string body = Body(OpenAiRequestBuilder.BuildResponsesRequest(
            request, "https://api.openai.com/v1", NullLogger.Instance));

        await Assert.That(body).IsEqualTo(
            """
            {"model":"gpt-5","input":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"},{"role":"assistant","content":"sure"},{"type":"function_call_output","call_id":"call_1","output":"body"}],"stream":true,"max_output_tokens":256,"reasoning":{"effort":"high"},"tools":[{"type":"function","name":"read","description":"Read a file.","parameters":{"type":"object","properties":{"path":{"type":"string"}}}}]}""")
            .Because("the Responses API calls the array `input`, names a tool result "
                   + "`function_call_output` with `call_id`/`output`, and takes the function "
                   + "fields inline instead of nested under `function`");
    }

    [Test]
    public async Task OpenAi_Responses_ToolCallingTurn_OmitsContent()
    {
        using var arguments = JsonDocument.Parse("""{"path":"a.txt"}""");

        string body = Body(OpenAiRequestBuilder.BuildResponsesRequest(
            Request(messages:
                [
                    new LlmAssistantMessage(
                        [new LlmToolCallBlock("c1", "read", arguments.RootElement)])
                ]),
            "https://api.openai.com/v1", NullLogger.Instance));

        using JsonDocument parsed = JsonDocument.Parse(body);
        await Assert.That(parsed.RootElement.GetProperty("input")[0].TryGetProperty("content", out _))
            .IsFalse()
            .Because("same WhenWritingNull semantics as the chat path — no content key at all");
    }

    // =================================================================
    // Ollama /api/chat
    // =================================================================

    [Test]
    public async Task Ollama_TextOnlyRequest_PinsEveryKey()
    {
        var request = Request(
            model: "llama3",
            messages: [LlmUserMessage.Text("hi")],
            maxTokens: 256,
            temperature: 0.4m,
            topP: 0.7m,
            topK: 30);

        await Assert.That(OllamaBody(request)).IsEqualTo(
            """
            {"model":"llama3","messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}],"stream":true,"options":{"temperature":0.4,"top_p":0.7,"top_k":30,"num_predict":256},"keep_alive":"5m"}""");
    }

    [Test]
    public async Task Ollama_NoOptions_StillEmitsTheEmptyObject()
    {
        await Assert.That(OllamaBody(Request(system: ""))).IsEqualTo(
            """
            {"model":"gpt-4o","messages":[],"stream":true,"options":{},"keep_alive":"5m"}""")
            .Because("an absent options key is a different request from an empty one for a local "
                   + "model, so the object is written unconditionally");
    }

    [Test]
    public async Task Ollama_ToolCallingTurn_SendsEmptyContentRatherThanOmittingIt()
    {
        using var arguments = JsonDocument.Parse("""{"path":"a.txt"}""");

        var request = Request(messages:
        [
            new LlmAssistantMessage([new LlmToolCallBlock("call_1", "read", arguments.RootElement)]),
            new LlmToolResultMessage("call_1", "read", "body", isError: false)
        ]);

        using JsonDocument parsed = JsonDocument.Parse(OllamaBody(request));
        JsonElement[] messages = parsed.RootElement.GetProperty("messages");

        await Assert.That(messages[0].GetProperty("content").GetString()).IsEqualTo("")
            .Because("unlike the OpenAI builders this path substituted \"\" for a missing text "
                   + "block rather than dropping the key — Ollama's template expects it present");

        JsonElement call = messages[0].GetProperty("tool_calls")[0];
        await Assert.That(call.GetProperty("function").GetProperty("arguments").GetString())
            .IsEqualTo("""{"path":"a.txt"}""");

        await Assert.That(messages[1].GetProperty("role").GetString()).IsEqualTo("tool");
        await Assert.That(messages[1].GetProperty("content").GetString()).IsEqualTo("body");
        await Assert.That(messages[1].TryGetProperty("tool_call_id", out _)).IsFalse()
            .Because("the Ollama dialect has never carried tool_call_id; that pre-existing "
                   + "difference from the OpenAI builders is pinned here so the writer cannot "
                   + "quietly 'fix' it as a side effect of this change");
    }

    [Test]
    public async Task Ollama_KeepAlive_FollowsTheConfiguredValue()
    {
        string body = OllamaBody(Request(system: ""), new OllamaConfig { KeepAlive = "30m" });

        await Assert.That(body).Contains("\"keep_alive\":\"30m\"");
    }
}
