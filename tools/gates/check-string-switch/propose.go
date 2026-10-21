package stringswitch

// propose.go -- the table a switch would become, and the machine-readable
// census.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
)

// switchBody is the (brace, body_end) of the switch whose keyword is at
// index.
func switchBody(masked string, index int) (int, int) {
	after := index + len("switch")
	for after < len(masked) && isSpaceByte(masked[after]) {
		after++
	}
	if after >= len(masked) || masked[after] != '(' {
		return -1, -1
	}
	close := matchBracket(masked, after, '(', ')')
	if close < 0 {
		return -1, -1
	}
	brace := close + 1
	for brace < len(masked) && isSpaceByte(masked[brace]) {
		brace++
	}
	if brace >= len(masked) || masked[brace] != '{' {
		return -1, -1
	}
	bodyEnd := matchBracket(masked, brace, '{', '}')
	if bodyEnd < 0 {
		return -1, -1
	}
	return brace, bodyEnd
}

// proposeTable is the FrozenDictionary initializer that would replace the
// site, or the reason there is none.
//
// Only the shape where every arm reduces to ONE value expression is
// proposed. An arm that assigns two locals, carries a `when` guard, or
// does something other than produce a value is reported as not mechanical
// -- the tool does not guess.
func proposeTable(s site, source string) (string, string) {
	if s.kind != "statement" {
		return "", "a switch EXPRESSION has no arms to group; hand it."
	}
	if s.guarded {
		return "", "an arm carries a `when` guard; the guard is part of the dispatch."
	}

	masked := maskSource(source)
	index := -1
	for _, candidate := range switchKeywordPositions(masked) {
		if strings.Count(source[:candidate], "\n")+1 == s.line {
			index = candidate
			break
		}
	}
	if index < 0 {
		return "", "the switch could not be re-located in the file."
	}

	brace, bodyEnd := switchBody(masked, index)
	if brace < 0 {
		return "", "the switch body could not be located."
	}

	type group struct {
		labels []string
		body   string
	}
	var groups []group
	var run []string
	runEnd := 0
	defaultBody := ""
	hasDefault := false

	for _, m := range armMarks(masked, brace+1, bodyEnd) {
		if m.kind == "case" {
			cursor := m.at + 4
			for cursor < len(source) && isSpaceByte(source[cursor]) {
				cursor++
			}
			value, end, ok := readLiteral(source, cursor)
			if !ok {
				run = nil
				continue
			}
			tail := end
			for tail < len(source) && isSpaceByte(source[tail]) {
				tail++
			}
			if strings.HasPrefix(source[tail:], "when") {
				return "", "an arm carries a `when` guard; the guard is part of the dispatch."
			}
			if tail < len(source) && source[tail] == ':' {
				tail++
			}
			// A label joins the run above it only when that run has no body
			// yet: `case "a": case "b": <body>` is one arm with two labels.
			if len(run) > 0 && strings.TrimSpace(masked[runEnd:m.at]) != "" {
				groups = append(groups, group{labels: run, body: source[runEnd:m.at]})
				run = nil
			}
			run = append(run, value)
			runEnd = tail
			continue
		}
		// A `default` (or an expression arm) closes the run of case labels.
		if len(run) > 0 {
			groups = append(groups, group{labels: run, body: source[runEnd:m.at]})
			run = nil
		}
		if m.kind == "default" {
			defaultBody = source[m.at+len("default") : bodyEnd]
			hasDefault = true
		} else {
			return "", "the switch body has an expression arm; hand it."
		}
	}
	if len(run) > 0 {
		groups = append(groups, group{labels: run, body: source[runEnd:bodyEnd]})
	}
	if len(groups) == 0 {
		return "", "the switch has no string-labelled arms."
	}

	var lines []string
	for _, g := range groups {
		value, ok := armValue(g.body)
		if !ok {
			return "", fmt.Sprintf("an arm body is not a single value expression: %s",
				pythonRepr(truncateChars(normalize(g.body), 60)))
		}
		for _, label := range g.labels {
			lines = append(lines, fmt.Sprintf("    [\"%s\"] = %s,", label, value))
		}
	}

	body := strings.Join(lines, "\n")
	if hasDefault {
		value, ok := armValue(defaultBody)
		if !ok {
			body += fmt.Sprintf("\n    // the default arm is NOT a table entry: %s",
				pythonRepr(truncateChars(normalize(defaultBody), 60)))
		} else {
			body += fmt.Sprintf("\n    // the default arm answers %s", pythonRepr(value))
		}
	}
	return body, ""
}

// armValue is the single value an arm body produces.
func armValue(body string) (string, bool) {
	text := strings.TrimSpace(trailingBreak.ReplaceAllString(strings.TrimSpace(body), ""))
	text = strings.TrimSpace(text)
	if m := armReturn.FindStringSubmatch(text); m != nil {
		return strings.TrimSpace(m[1]), true
	}
	if m := armAssign.FindStringSubmatch(text); m != nil {
		return strings.TrimSpace(m[4]), true
	}
	return "", false
}

// switchKeywordPositions returns the positions of the `switch` keywords
// that are not part of a longer word.
//
// The python original's pattern is `(?<![\w.])switch(?![\w])`; the
// boundaries are checked here, because RE2 has no lookaround.
func switchKeywordPositions(masked string) []int {
	var out []int
	for _, match := range switchKeyword.FindAllStringIndex(masked, -1) {
		index := match[0]
		if index > 0 && isWordOrDot(masked[index-1]) {
			continue
		}
		if index+6 < len(masked) && isWordByte(masked[index+6]) {
			continue
		}
		out = append(out, index)
	}
	return out
}

// pythonRepr is python's `repr` of a string.
func pythonRepr(s string) string {
	return "'" + strings.ReplaceAll(s, "'", "\\'") + "'"
}

// truncateChars cuts a string to width characters.
func truncateChars(s string, width int) string {
	runes := []rune(s)
	if len(runes) <= width {
		return s
	}
	return string(runes[:width])
}

// reportJSON prints the census as JSON.
func reportJSON(sites []site) int {
	var statements, expressions []site
	labels := 0
	for _, s := range sites {
		if s.kind == "statement" {
			statements = append(statements, s)
		} else {
			expressions = append(expressions, s)
		}
		labels += len(s.labels)
	}
	statementLabels := 0
	for _, s := range statements {
		statementLabels += len(s.labels)
	}
	expressionLabels := 0
	for _, s := range expressions {
		expressionLabels += len(s.labels)
	}

	var b strings.Builder
	b.WriteString("{\n")
	b.WriteString(fmt.Sprintf("  \"switches\": %d,\n", len(sites)))
	b.WriteString(fmt.Sprintf("  \"statements\": %d,\n", len(statements)))
	b.WriteString(fmt.Sprintf("  \"expressions\": %d,\n", len(expressions)))
	b.WriteString(fmt.Sprintf("  \"labels\": %d,\n", labels))
	b.WriteString(fmt.Sprintf("  \"statement_labels\": %d,\n", statementLabels))
	b.WriteString(fmt.Sprintf("  \"expression_labels\": %d,\n", expressionLabels))
	b.WriteString("  \"sites\": [\n")
	for i, s := range sites {
		b.WriteString("    {\n")
		b.WriteString(fmt.Sprintf("      \"path\": %s,\n", jsonString(s.path)))
		b.WriteString(fmt.Sprintf("      \"line\": %d,\n", s.line))
		b.WriteString(fmt.Sprintf("      \"kind\": %s,\n", jsonString(s.kind)))
		b.WriteString(fmt.Sprintf("      \"selector\": %s,\n", jsonString(s.selector)))
		b.WriteString("      \"labels\": [")
		if len(s.labels) > 0 {
			b.WriteString("\n")
			for j, label := range s.labels {
				b.WriteString("        ")
				b.WriteString(jsonString(label))
				if j < len(s.labels)-1 {
					b.WriteString(",")
				}
				b.WriteString("\n")
			}
			b.WriteString("      ")
		}
		b.WriteString("],\n")
		b.WriteString(fmt.Sprintf("      \"guarded\": %s,\n", jsonBool(s.guarded)))
		b.WriteString(fmt.Sprintf("      \"has_default\": %s\n", jsonBool(s.hasDefault)))
		if i < len(sites)-1 {
			b.WriteString("    },\n")
		} else {
			b.WriteString("    }\n")
		}
	}
	b.WriteString("  ]\n")
	b.WriteString("}")
	fmt.Println(b.String())
	return gate.Green
}

// jsonString renders a string, the way python's json does.
func jsonString(s string) string {
	var b strings.Builder
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case '"':
			b.WriteString("\\\"")
		case '\\':
			b.WriteString("\\\\")
		case '\n':
			b.WriteString("\\n")
		case '\r':
			b.WriteString("\\r")
		case '\t':
			b.WriteString("\\t")
		case '\b':
			b.WriteString("\\b")
		case '\f':
			b.WriteString("\\f")
		default:
			if r < 0x20 || r > 0x7e {
				b.WriteString(fmt.Sprintf("\\u%04x", r))
				continue
			}
			b.WriteRune(r)
		}
	}
	b.WriteByte('"')
	return b.String()
}

// jsonBool renders a boolean.
func jsonBool(b bool) string {
	if b {
		return "true"
	}
	return "false"
}
