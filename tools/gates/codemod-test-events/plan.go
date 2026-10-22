package codemodtestevents

// plan.go -- the decision: what to write in place of a construction.
//
// The rule the codemod enforces is one sentence: a rewritten call must MEAN
// what the hand-written one meant. So nothing is guessed. A factory
// parameter has a DEFAULT, and the argument is left out only when the source
// value IS that default; a form the codemod does not understand is left
// alone and reported with the reason.

import (
	"fmt"
	"regexp"
	"strings"
)

// param is one factory parameter: its name, the value to pass, and the
// default.
type param struct {
	name        string
	value       string
	def         string
	emptyOK     bool
	substitutes bool
}

// plan is what to write in place of a construction.
type plan struct {
	method string
	params []param
}

// messageParam is one parameter of a message builder: its name, its value
// and its default.
type messageParam struct {
	name        string
	value       string
	def         string
	substitutes bool
}

// messagePlan is what to write in place of a message construction.
type messagePlan struct {
	method  string
	payload string
	params  []messageParam
}

// buildParams maps a constructor's arguments onto a builder's parameters.
//
// The mapping follows C#'s own rules for a call that mixes positional and
// named arguments:
//
//   - a positional argument takes the next parameter that no earlier
//     argument filled;
//   - a named argument takes the parameter of that name, and a named
//     argument that comes after a positional one must name a parameter at
//     or after the position the positional arguments reached — which is
//     what lets `new AgentEndEvent(messages, Limit: X)` skip the optional
//     `Cancelled` in between;
//   - a positional argument that comes after a named one is only legal when
//     the named argument sat in its own position, and it then takes the
//     parameter right after it.
//
// The second return value is false when the arguments do not fit.
func buildParams(args []argument, spec []paramSpec) ([]param, bool) {
	params := make([]param, 0, len(spec))
	for _, s := range spec {
		params = append(params, param{
			name:        s.name,
			value:       s.def,
			def:         s.def,
			emptyOK:     s.emptyOK,
			substitutes: s.substitutes,
		})
	}
	allNamed := true
	for _, a := range args {
		if !a.named {
			allNamed = false
			break
		}
	}
	nextPositional := 0

	for _, a := range args {
		if a.named {
			index := -1
			for i := range params {
				if strings.EqualFold(params[i].name, a.name) {
					index = i
					break
				}
			}
			if index == -1 {
				return nil, false
			}
			if !allNamed && index < nextPositional {
				return nil, false
			}
			params[index].value = a.value
			if index+1 > nextPositional {
				nextPositional = index + 1
			}
			continue
		}
		if nextPositional >= len(params) {
			return nil, false
		}
		params[nextPositional].value = a.value
		nextPositional++
	}

	return params, true
}

// nullsAreRepresentable whether an explicit `null` survives the builder's
// default.
//
// A builder that substitutes a value for a `null` argument would CHANGE the
// event: `new MessageStartEvent(null!)` means "a null message", and
// `TestEvents.MessageStart(null!)` would mean "the default message". The
// parameters whose builder substitutes a value for a null are marked in the
// spec, and a site that passes one of them a null is left alone.
func nullsAreRepresentable(params []param) bool {
	for _, p := range params {
		if isNull(p.value) && p.substitutes {
			return false
		}
	}
	return true
}

// jsonArgsPattern is the arguments element the codemod has to RECONSTRUCT
// rather than copy.
var jsonArgsPattern = regexp.MustCompile(
	`(?s)^(?:System\.Text\.Json\.)?JsonDocument\.Parse\((.*)\)\.RootElement\.Clone\(\)$`)

// planFor the plan for one construction, and the reason there is none.
func planFor(typename string, args []argument) (*plan, string) {
	entry, ok := eventSpecs[typename]
	if !ok {
		return nil, typename + ": no builder for this type"
	}

	if typename == "ToolExecutionStartEvent" {
		// The third argument is a JsonElement the caller already owns. The
		// builder takes the JSON as a string and clones it inside, so the
		// argument has to be a literal the codemod can read.
		if len(args) < 3 {
			return nil, typename + ": no arguments element"
		}
		m := jsonArgsPattern.FindStringSubmatch(pyStrip(args[2].value))
		if m == nil {
			return nil, typename + ": the arguments element is not a readable JSON literal"
		}
		literal := pyStrip(m[1])
		if !strings.HasPrefix(literal, `"`) {
			return nil, typename + ": the arguments element is not a JSON string literal"
		}
		args = append([]argument{}, args...)
		args[2] = argument{name: args[2].name, value: literal, named: args[2].named}
	}

	params, ok := buildParams(args, entry.params)
	if !ok {
		return nil, typename + ": the argument shape is not one the builder can reproduce"
	}
	if !nullsAreRepresentable(params) {
		return nil, typename + ": an argument is null and the builder substitutes a default for it"
	}
	return &plan{method: entry.method, params: params}, ""
}

// planForUpdate the plan for a `MessageUpdateEvent` wrapping a provider-level
// event.
func planForUpdate(typename string, args []argument) (*plan, string) {
	if len(args) != 2 {
		return nil, "MessageUpdateEvent: not exactly two arguments"
	}
	innerType, innerArgs, ok := parseNewExpression(args[0].value)
	if !ok {
		return nil, "MessageUpdateEvent: the first argument is not a construction"
	}
	entry, known := eventSpecs[innerType]
	if !known {
		return nil, fmt.Sprintf("MessageUpdateEvent: %s has no builder", innerType)
	}
	if _, wrapped := updateFactories[innerType]; !wrapped {
		return nil, fmt.Sprintf("MessageUpdateEvent: %s has no wrapped builder", innerType)
	}
	if isNull(args[1].value) {
		return nil, "MessageUpdateEvent: the partial message is null"
	}

	params, ok := buildParams(innerArgs, entry.params)
	if !ok {
		return nil, fmt.Sprintf("MessageUpdateEvent: the %s arguments are not reproducible", innerType)
	}
	if !nullsAreRepresentable(params) {
		return nil, fmt.Sprintf("MessageUpdateEvent: an argument of the %s is null", innerType)
	}
	for _, dropped := range updateDroppedParams[innerType] {
		for _, p := range params {
			if p.name == dropped && !isDefault(p) {
				return nil, fmt.Sprintf("MessageUpdateEvent: %s names a %s", innerType, dropped)
			}
		}
		kept := params[:0:0]
		for _, p := range params {
			if p.name != dropped {
				kept = append(kept, p)
			}
		}
		params = kept
	}

	params = append(params, param{
		name:        "partial",
		value:       args[1].value,
		def:         defaultEmptyAssistant,
		substitutes: true,
	})
	return &plan{method: updateFactories[innerType], params: params}, ""
}

// planForMessageUpdate the fallback for a `MessageUpdateEvent` whose payload
// is not a construction.
func planForMessageUpdate(args []argument) (*plan, string) {
	if len(args) != 2 {
		return nil, "MessageUpdateEvent: not exactly two arguments"
	}
	if isNull(args[1].value) {
		return nil, "MessageUpdateEvent: the partial message is null"
	}

	payload := pyStrip(args[0].value)
	innerType, innerArgs, ok := parseNewExpression(payload)
	if ok {
		entry, known := eventSpecs[innerType]
		if !known {
			return nil, fmt.Sprintf("MessageUpdateEvent: %s has no builder", innerType)
		}
		innerParams, fitted := buildParams(innerArgs, entry.params)
		if !fitted {
			return nil, fmt.Sprintf("MessageUpdateEvent: the %s arguments are not reproducible", innerType)
		}
		if !nullsAreRepresentable(innerParams) {
			return nil, fmt.Sprintf("MessageUpdateEvent: an argument of the %s is null", innerType)
		}
		payload = emit(entry.method, innerParams)
	}

	return &plan{
		method: "MessageUpdate",
		params: []param{
			{name: "llmEvent", value: payload},
			{name: "partial", value: args[1].value, def: defaultEmptyAssistant, substitutes: true},
		},
	}, ""
}

// resolveMessageArgs the values a construction passes, keyed by the record's
// parameter name.
//
// The second return value is false when the arguments do not fit the record —
// a name the record does not have, or one argument too many.
func resolveMessageArgs(typename string, args []argument) (map[string]string, bool) {
	names := recordParams[typename]
	values := map[string]string{}
	nextPositional := 0
	for _, a := range args {
		if a.named {
			index := -1
			for i := range names {
				if strings.EqualFold(names[i], a.name) {
					index = i
					break
				}
			}
			if index == -1 {
				return nil, false
			}
			values[names[index]] = a.value
			if index+1 > nextPositional {
				nextPositional = index + 1
			}
			continue
		}
		if nextPositional >= len(names) {
			return nil, false
		}
		values[names[nextPositional]] = a.value
		nextPositional++
	}
	return values, true
}

// planForMessage the plan for one message construction, and the reason there
// is none.
func planForMessage(typename string, args []argument) (*messagePlan, string) {
	entry, ok := messageSpecs[typename]
	if !ok {
		return nil, typename + ": no builder for this type"
	}
	method := entry.method

	// A comment inside a construction is left alone. The comma-split puts a
	// trailing comment at the HEAD of the next argument, and the message
	// factory moves the payload to the front — so the comment would travel
	// to a place where it reads as if it documented something else. Leaving
	// the construction alone keeps the comment where its author put it.
	for _, a := range args {
		if hasComment(a.value) {
			return nil, typename + ": a comment inside the construction"
		}
	}

	values, ok := resolveMessageArgs(typename, args)
	if !ok {
		return nil, typename + ": the argument shape is not one the builder can reproduce"
	}

	payload, hasPayload := values[entry.payload]
	if !hasPayload {
		return nil, typename + ": the payload argument is missing"
	}

	if typename == "AssistantMessage" {
		text, isText := singleTextPart(payload)
		if isText {
			method = "Assistant"
			payload = text
		} else {
			method = "AssistantParts"
		}
	}

	var params []messageParam
	for _, ps := range entry.params {
		value, named := values[ps.record]
		if !named {
			// The source did not name the parameter, so the RECORD's default
			// applies. It is the same default the builder substitutes, so the
			// argument is left out.
			if !recordDefaults[ps.record] {
				return nil, fmt.Sprintf("%s: the %s argument is missing", typename, ps.record)
			}
			continue
		}
		if isNull(value) && ps.substitutes {
			return nil, fmt.Sprintf(
				"%s: the %s argument is null and the builder substitutes a value for it",
				typename, ps.record)
		}
		params = append(params, messageParam{
			name:        ps.factory,
			value:       value,
			def:         ps.def,
			substitutes: ps.substitutes,
		})
	}

	return &messagePlan{method: method, payload: payload, params: params}, ""
}

var (
	textPartPattern  = regexp.MustCompile(`(?s)^new\s+TextPart\s*\((.*)\)$`)
	arrayInitPattern = regexp.MustCompile(`(?s)^new\s*(?:\w+\s*)?\[\s*\]\s*\{(.*)\}$`)
)

// singleTextPart the text of a parts expression that is exactly one text
// part.
//
// The second return value is false when the expression is not one text part
// — then the caller keeps the parts list as it is.
func singleTextPart(parts string) (string, bool) {
	text := pyStrip(parts)
	var inner string
	if strings.HasPrefix(text, "[") && strings.HasSuffix(text, "]") {
		inner = text[1 : len(text)-1]
	} else {
		m := arrayInitPattern.FindStringSubmatch(text)
		if m == nil {
			return "", false
		}
		inner = m[1]
	}

	var elements []argument
	for _, a := range splitArguments(inner) {
		if pyStrip(a.value) != "" {
			elements = append(elements, a)
		}
	}
	if len(elements) != 1 {
		return "", false
	}
	m := textPartPattern.FindStringSubmatch(pyStrip(elements[0].value))
	if m == nil {
		return "", false
	}
	literal := pyStrip(m[1])
	if !strings.HasPrefix(literal, `"`) && !strings.HasPrefix(literal, `$"`) {
		return "", false
	}
	return literal, true
}

// isDefault whether the argument can be left out because it equals the
// default.
func isDefault(p param) bool {
	if p.emptyOK && isEmptyList(p.value) {
		return true
	}
	if p.def == "" {
		return false
	}
	return sameValue(p.value, p.def)
}

// emit writes the call, omitting every argument that equals its default.
func emit(method string, params []param) string {
	var kept []int
	for index, p := range params {
		if !isDefault(p) {
			kept = append(kept, index)
		}
	}
	var rendered []string
	nextPositional := 0
	for _, index := range kept {
		p := params[index]
		value := p.value
		if strings.Contains(value, "\n") {
			if folded, ok := collapseWhitespace(value); ok {
				value = folded
			}
		}
		if index == nextPositional {
			rendered = append(rendered, value)
			nextPositional++
			continue
		}
		rendered = append(rendered, p.name+": "+value)
	}
	return "TestEvents." + method + "(" + strings.Join(rendered, ", ") + ")"
}

// fold the argument on one line, when folding it does not lose a comment.
func fold(value string) string {
	if !strings.Contains(value, "\n") {
		return value
	}
	folded, ok := collapseWhitespace(value)
	if !ok {
		return value
	}
	return folded
}

// emitMessage writes the message call.
//
// The payload is positional — it is the argument every call site passes and
// the one the reader looks for. Every other argument is NAMED, because a
// message record's adjacent parameters are often the same type: `agent` and
// `model` are both `string`, `id` and `sessionId` are both `string`, and a
// positional call can swap them and still compile.
func emitMessage(p *messagePlan) string {
	rendered := []string{fold(p.payload)}
	for _, mp := range p.params {
		if mp.def != "" && sameValue(mp.value, mp.def) {
			continue
		}
		rendered = append(rendered, mp.name+": "+fold(mp.value))
	}
	return "TestMessages." + p.method + "(" + strings.Join(rendered, ", ") + ")"
}
