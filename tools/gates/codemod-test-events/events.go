package codemodtestevents

// events.go -- the data the codemod is built from: the event types it knows
// how to build, the builder each one maps to, the parameter of each builder
// and the default the builder substitutes.
//
// The data is the python original's SPECS / MESSAGE_SPECS / RECORD_PARAMS
// tables, unchanged. A builder whose parameter list drifts from the
// builder's real signature is a codemod that writes code that does not
// compile, so the tables are written out rather than derived.

// The scope.
const scopeDir = "tests"

// The directories the codemod does not touch.
var excludedDirs = []string{
	"tests/Harbor.Architecture.Tests",
	// The kit's own sources DEFINE the builders. Rewriting them would be
	// self-referential — the codemod would turn the factory's body into calls
	// to the factory.
	"tests/Harbor.TestKit",
	"tests/Harbor.TestKit.Support",
	"tests/Harbor.TestKit.Tui",
}

// The event types the codemod knows how to build. Anything else is left
// alone.
var domainEvents = []string{
	"AgentStartEvent",
	"AgentEndEvent",
	"AgentErrorEvent",
	"TurnStartEvent",
	"TurnEndEvent",
	"MessageStartEvent",
	"MessageEndEvent",
	"MessageUpdateEvent",
	"ToolExecutionStartEvent",
	"ToolExecutionUpdateEvent",
	"ToolExecutionEndEvent",
	"SessionStatsEvent",
	"SessionChangedEvent",
	"CompactionStartedEvent",
	"CompactionCompletedEvent",
	"CompactionFailedEvent",
	"PluginBlockedEvent",
}

var llmEvents = []string{
	"TextStartEvent",
	"TextDeltaEvent",
	"TextEndEvent",
	"ThinkingStartEvent",
	"ThinkingDeltaEvent",
	"ThinkingEndEvent",
	"ToolCallStartEvent",
	"ToolCallDeltaEvent",
	"StepStartEvent",
	"StepFinishEvent",
	"FinishEvent",
	"ErrorEvent",
}

// The MESSAGE types the codemod knows how to build. Anything else is left
// alone.
var messageTypes = []string{
	"UserMessage",
	"AssistantMessage",
	"ToolResultMessage",
}

var (
	knownEvents = append(append([]string{}, domainEvents...), llmEvents...)
	knownTypes  = append(append([]string{}, knownEvents...), messageTypes...)
)

// isMessageType whether the type is one of the MESSAGE records.
func isMessageType(name string) bool {
	for _, m := range messageTypes {
		if m == name {
			return true
		}
	}
	return false
}

// The defaults, and the text that stands for each of them.
const (
	defaultSessionID       = `"s1"`
	defaultToolCallID      = `"tc1"`
	defaultLLMToolCallID   = `"call-1"`
	defaultToolName        = `"counter"`
	defaultEmptyAssistant  = `AssistantMessage.Empty("s1", "m")`
	defaultMetadata        = "SessionMetadata.Empty"
	defaultEmptyArgs       = `"{}"`
	defaultMessageID       = `Guid.NewGuid().ToString("N")`
	defaultMessageSession  = `"session-1"`
	defaultMessageCreated  = "DateTimeOffset.UtcNow"
	defaultMessageAgent    = `"code"`
	defaultMessageModel    = `"test-model"`
	defaultMessageStop     = "StopReason.Stop"
	defaultMessageUsage    = "new Usage(0, 0)"
	defaultMessageParent   = "null"
	defaultMessageAttach   = "null"
	defaultMessageSummary  = "false"
	defaultMessageSummaryK = "null"
	emptyList              = "[]"
)

// paramSpec is one builder parameter: its name, the default text the
// builder substitutes, "an empty list counts as the default" and "the
// builder substitutes a non-null value for a null".
type paramSpec struct {
	name        string
	def         string
	emptyOK     bool
	substitutes bool
}

// eventSpec is one event type: the builder it becomes and its parameters.
type eventSpec struct {
	method string
	params []paramSpec
}

// messageParamSpec is one message builder parameter: the factory name, the
// record parameter it carries, the default text and "the builder
// substitutes a value for a null".
type messageParamSpec struct {
	factory     string
	record      string
	def         string
	substitutes bool
}

// messageSpec is one message type: the builder it becomes, the record
// parameter that carries the payload, and its parameters.
type messageSpec struct {
	method  string
	payload string
	params  []messageParamSpec
}

// The specs, per event type.
//
// The default text is what the BUILDER substitutes, so a source argument
// equal to it can be left out.
var eventSpecs = map[string]eventSpec{
	"AgentStartEvent": {
		method: "AgentStart",
		params: []paramSpec{
			{"sessionId", defaultSessionID, false, false},
			{"messages", emptyList, true, true},
			{"model", "null", false, false},
			{"kind", "SessionKind.User", false, false},
		},
	},
	"AgentEndEvent": {
		method: "AgentEnd",
		params: []paramSpec{
			{"messages", emptyList, true, true},
			{"cancelled", "false", false, false},
			{"limit", "null", false, false},
		},
	},
	"AgentErrorEvent": {
		method: "AgentError",
		params: []paramSpec{
			{"message", `"boom"`, false, false},
			{"exception", "null", false, false},
		},
	},
	"TurnStartEvent": {
		method: "TurnStart",
		params: []paramSpec{
			{"turnIndex", "1", false, false},
			{"sessionId", "null", false, false},
		},
	},
	"TurnEndEvent": {
		method: "TurnEnd",
		params: []paramSpec{
			{"message", defaultEmptyAssistant, false, true},
			{"toolResults", emptyList, true, true},
			{"sessionId", "null", false, false},
		},
	},
	"MessageStartEvent": {
		method: "MessageStart",
		params: []paramSpec{{"message", defaultEmptyAssistant, false, true}},
	},
	"MessageEndEvent": {
		method: "MessageEnd",
		params: []paramSpec{{"message", defaultEmptyAssistant, false, true}},
	},
	"ToolExecutionStartEvent": {
		method: "ToolExecutionStart",
		params: []paramSpec{
			{"toolCallId", defaultToolCallID, false, false},
			{"toolName", `"read"`, false, false},
			{"argsJson", defaultEmptyArgs, false, true},
			{"glyph", "null", false, false},
		},
	},
	"ToolExecutionUpdateEvent": {
		method: "ToolExecutionUpdate",
		params: []paramSpec{
			{"toolCallId", defaultToolCallID, false, false},
			{"partialResult", `""`, false, true},
			{"retryAttempt", "null", false, false},
			{"retryMaxAttempts", "null", false, false},
			{"retryBackoffSeconds", "null", false, false},
		},
	},
	"ToolExecutionEndEvent": {
		method: "ToolExecutionEnd",
		params: []paramSpec{
			{"toolCallId", defaultToolCallID, false, false},
			{"result", "null", false, true},
			{"isError", "false", false, false},
		},
	},
	"SessionStatsEvent": {
		method: "SessionStats",
		params: []paramSpec{
			{"sessionId", defaultSessionID, false, false},
			{"metadata", defaultMetadata, false, true},
		},
	},
	"SessionChangedEvent": {
		method: "SessionChanged",
		params: []paramSpec{{"sessionId", defaultSessionID, false, false}},
	},
	"CompactionStartedEvent": {
		method: "CompactionStarted",
		params: []paramSpec{{"sessionId", defaultSessionID, false, false}},
	},
	"CompactionCompletedEvent": {
		method: "CompactionCompleted",
		params: []paramSpec{
			{"sessionId", defaultSessionID, false, false},
			{"summary", `"summary"`, false, false},
			{"prunedMessageCount", "0", false, false},
			{"tokensSaved", "0", false, false},
			{"duration", "TimeSpan.FromSeconds(1)", false, false},
		},
	},
	"CompactionFailedEvent": {
		method: "CompactionFailed",
		params: []paramSpec{
			{"sessionId", defaultSessionID, false, false},
			{"error", `"boom"`, false, false},
		},
	},
	"PluginBlockedEvent": {
		method: "PluginBlocked",
		params: []paramSpec{
			{"pluginName", `"plugin"`, false, false},
			{"reason", `"timeout"`, false, false},
			{"detail", `"detail"`, false, false},
		},
	},
	"TextStartEvent": {
		method: "TextStart",
		params: []paramSpec{{"id", `"t"`, false, false}},
	},
	"TextDeltaEvent": {
		method: "TextDelta",
		params: []paramSpec{
			{"id", `"t"`, false, false},
			{"delta", `""`, false, false},
		},
	},
	"TextEndEvent": {
		method: "TextEnd",
		params: []paramSpec{
			{"id", `"t"`, false, false},
			{"finalText", `""`, false, false},
		},
	},
	"ThinkingStartEvent": {
		method: "ThinkingStart",
		params: []paramSpec{{"id", `"h1"`, false, false}},
	},
	"ThinkingDeltaEvent": {
		method: "ThinkingDelta",
		params: []paramSpec{
			{"id", `"h1"`, false, false},
			{"delta", `""`, false, false},
		},
	},
	"ThinkingEndEvent": {
		method: "ThinkingEnd",
		params: []paramSpec{
			{"id", `"h1"`, false, false},
			{"finalText", `""`, false, false},
		},
	},
	"ToolCallStartEvent": {
		method: "ToolCallStart",
		params: []paramSpec{
			{"id", defaultLLMToolCallID, false, false},
			{"toolName", defaultToolName, false, false},
		},
	},
	"ToolCallDeltaEvent": {
		method: "ToolCallDelta",
		params: []paramSpec{
			{"id", defaultLLMToolCallID, false, false},
			{"argsDelta", defaultEmptyArgs, false, false},
		},
	},
	"StepStartEvent": {
		method: "StepStart",
		params: []paramSpec{{"index", "0", false, false}},
	},
	"StepFinishEvent": {
		method: "StepFinish",
		params: []paramSpec{
			{"index", "0", false, false},
			{"finishReason", `"stop"`, false, false},
			{"usage", "null", false, false},
			{"providerModelId", "null", false, false},
		},
	},
	"FinishEvent": {
		method: "LlmFinish",
	},
	"ErrorEvent": {
		method: "LlmError",
		params: []paramSpec{
			{"message", `"boom"`, false, false},
			{"exception", "null", false, false},
			{"kind", "ProviderErrorKind.Unknown", false, false},
			{"statusCode", "null", false, false},
		},
	},
}

// The provider-level events that also have a "wrapped in an update" builder.
var updateFactories = map[string]string{
	"TextDeltaEvent":     "TextDeltaUpdate",
	"ThinkingDeltaEvent": "ThinkingDeltaUpdate",
	"ToolCallStartEvent": "ToolCallStartUpdate",
	"ToolCallDeltaEvent": "ToolCallDeltaUpdate",
	"StepFinishEvent":    "StepFinishUpdate",
}

// The `StepFinish` builder inside an update has no `providerModelId` — a
// step finish that names a provider model is left alone rather than
// half-migrated.
var updateDroppedParams = map[string][]string{
	"StepFinishEvent": {"providerModelId"},
}

// The parameters of the MESSAGE records, in the records' own order.
//
// The codemod needs them to read a construction that names its arguments
// (`new UserMessage(Id: "m1", ...)`) and to know which parameter each
// positional argument fills.
var recordParams = map[string][]string{
	"UserMessage": {
		"Id", "SessionId", "CreatedAt", "Content", "Agent", "Model",
		"ParentId", "Attachments",
	},
	"AssistantMessage": {
		"Id", "SessionId", "CreatedAt", "Parts", "StopReason", "Usage",
		"Model", "ParentId", "IsSummary", "SummaryFirstKeptId",
	},
	"ToolResultMessage": {
		"Id", "SessionId", "CreatedAt", "Results", "ParentId",
	},
}

// The record parameters the record itself gives a default. A source that
// did not name one of them means the record's default, which is what the
// builder substitutes too.
var recordDefaults = map[string]bool{
	"ParentId":           true,
	"Attachments":        true,
	"IsSummary":          true,
	"SummaryFirstKeptId": true,
}

// The message specs.
var messageSpecs = map[string]messageSpec{
	"UserMessage": {
		method:  "User",
		payload: "Content",
		params: []messageParamSpec{
			{"id", "Id", defaultMessageID, true},
			{"sessionId", "SessionId", defaultMessageSession, true},
			{"createdAt", "CreatedAt", defaultMessageCreated, true},
			{"agent", "Agent", defaultMessageAgent, true},
			{"model", "Model", defaultMessageModel, true},
			{"parentId", "ParentId", defaultMessageParent, false},
			{"attachments", "Attachments", defaultMessageAttach, false},
		},
	},
	"AssistantMessage": {
		method:  "Assistant",
		payload: "Parts",
		params: []messageParamSpec{
			{"id", "Id", defaultMessageID, true},
			{"sessionId", "SessionId", defaultMessageSession, true},
			{"createdAt", "CreatedAt", defaultMessageCreated, true},
			{"stopReason", "StopReason", defaultMessageStop, true},
			{"usage", "Usage", defaultMessageUsage, true},
			{"model", "Model", defaultMessageModel, true},
			{"parentId", "ParentId", defaultMessageParent, false},
			{"isSummary", "IsSummary", defaultMessageSummary, true},
			{"summaryFirstKeptId", "SummaryFirstKeptId", defaultMessageSummaryK, false},
		},
	},
	"ToolResultMessage": {
		method:  "ToolResult",
		payload: "Results",
		params: []messageParamSpec{
			{"id", "Id", defaultMessageID, true},
			{"sessionId", "SessionId", defaultMessageSession, true},
			{"createdAt", "CreatedAt", defaultMessageCreated, true},
			{"parentId", "ParentId", defaultMessageParent, false},
		},
	},
}
