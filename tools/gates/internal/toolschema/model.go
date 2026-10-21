// Package toolschema is the ONE reading of a builtin tool's [ToolParam]
// declaration.
//
// It is the port of tools/tool_schema_model.py, and it exists for the same
// reason the python module existed: a builtin tool declares the JSON Schema
// of its arguments ONCE, as [ToolParam] attributes on the tool class, and
// the schema is the tool's contract with the model. It may not drift by a
// byte, so the rendered schema is hashed and pinned in
// src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json, and both the
// compiler (Harbor.CodeGen.ToolSchemaGenerator) and the gate
// (harbor-gates check-tool-schema-pin) compare against that one pin.
//
// THE CANONICAL FORM
//
//	The canonical form of a schema is its compact JSON text, with the keys in
//	DECLARATION order and the strings escaped the way
//	json.dumps(..., ensure_ascii=False) escapes them. It is a lossless
//	encoding of the schema's CONTENT, which is what the contract is: the
//	whitespace of the literal that carries it is not part of it.
//
//	The pin is the SHA-256 of that text, per tool, keyed by the tool's CLASS
//	name: McpToolAdapter's tool name is derived from the MCP server and the
//	tool it wraps, so it is not stable.
package toolschema

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// The paths the model reads, relative to the repository root.
const (
	// ToolsRoot holds the builtin tools' sources.
	ToolsRoot = "src/Harbor.Tools.Builtin/Tools"
	// SchemasRoot holds the pin.
	SchemasRoot = "src/Harbor.Tools.Builtin/Schemas"
	// PinName is the pin file's name.
	PinName = "tool-schemas.sha256.json"
	// PinRelative is the pin file's path, as the gate prints it.
	PinRelative = "src/Harbor.Tools.Builtin/Schemas/" + PinName
)

// The marker and the parameter attribute, matched by name in the source.
const schemaMarker = "[ToolSchema]"

// The parameter types the declaration may use. A type outside this set is
// a typo, and a typo that reaches the model is a wrong tool call.
var knownTypes = map[string]bool{
	"string": true, "integer": true, "boolean": true, "object": true, "array": true,
}

// The tool class. The anchor keeps a class declaration inside a doc comment
// (the example in ToolParamAttribute.cs) out of the result.
var toolClassRe = regexp.MustCompile(`(?m)^public sealed (partial )?class ([A-Za-z0-9_]+) : ITool`)

// The pieces of a [ToolParam(...)] argument list.
var (
	descriptionRe = regexp.MustCompile(`Description = "((?:[^"\\]|\\.)*)"`)
	enumRe        = regexp.MustCompile(`Enum = new\[\] \{ ([^}]*) \}`)
	itemsOfRe     = regexp.MustCompile(`ItemsOf = "([^"]*)"`)
	requiredRe    = regexp.MustCompile(`Required = true`)
)

// Failure is a refusal: the tree does not say what this tool expects it to
// say.
type Failure struct {
	Message string
}

func (f *Failure) Error() string { return f.Message }

func failuref(format string, args ...any) error {
	return &Failure{Message: fmt.Sprintf(format, args...)}
}

// Entry is one tool that declares a schema: the class name and the schema.
type Entry struct {
	Name   string
	Schema *Value
}

// Digest is the SHA-256 of a schema's canonical text, lower-case hex.
func Digest(schema *Value) string {
	return DigestText(schema.Canonical())
}

// DigestText is the SHA-256 of a text, lower-case hex.
func DigestText(text string) string {
	sum := sha256.Sum256([]byte(text))
	return hex.EncodeToString(sum[:])
}

// ToolFiles returns every source file under the builtin tools' directory.
func ToolFiles(root string) []string {
	base := filepath.Join(root, ToolsRoot)
	var found []string
	_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			return nil
		}
		if strings.HasSuffix(info.Name(), ".cs") {
			found = append(found, path)
		}
		return nil
	})
	sort.Strings(found)
	return found
}

// Collect returns every tool that declares a schema, with that schema.
//
// The list is sorted by the class name, and two tools that declare the same
// name are a refusal: the pin is keyed by that name, so a collision would
// make the pin a record of one of them.
func Collect(root string) ([]Entry, error) {
	var found []Entry
	for _, path := range ToolFiles(root) {
		text, err := os.ReadFile(path)
		if err != nil {
			return nil, err
		}
		relative := relativeTo(root, path)
		legacy, err := extractLegacy(relative, string(text))
		if err != nil {
			return nil, err
		}
		if legacy != nil {
			found = append(found, *legacy)
			continue
		}
		declared, err := readDeclaration(relative, string(text))
		if err != nil {
			return nil, err
		}
		if declared != nil {
			found = append(found, *declared)
		}
	}
	sort.SliceStable(found, func(i, j int) bool { return found[i].Name < found[j].Name })

	seen := map[string]bool{}
	duplicates := map[string]bool{}
	for _, entry := range found {
		if seen[entry.Name] {
			duplicates[entry.Name] = true
		}
		seen[entry.Name] = true
	}
	if len(duplicates) > 0 {
		names := make([]string, 0, len(duplicates))
		for name := range duplicates {
			names = append(names, name)
		}
		sort.Strings(names)
		return nil, failuref("two tools declare the same name: %s", strings.Join(names, ", "))
	}
	return found, nil
}

// ReadPins reads the pin file: one hash per tool.
//
// A missing file is an empty map, not an error: the gate reports the empty
// pin as a finding, and "the pin is missing" and "the pin is empty" are the
// same hole.
func ReadPins(root string) map[string]string {
	pins := map[string]string{}
	path := filepath.Join(root, SchemasRoot, PinName)
	data, err := os.ReadFile(path)
	if err != nil {
		return pins
	}
	text := string(data)
	index := 0
	for index < len(text) {
		openQuote := strings.Index(text[index:], `"`)
		if openQuote < 0 {
			break
		}
		openQuote += index
		closeQuote := strings.Index(text[openQuote+1:], `"`)
		if closeQuote < 0 {
			break
		}
		closeQuote += openQuote + 1
		key := text[openQuote+1 : closeQuote]
		colon := strings.Index(text[closeQuote:], ":")
		if colon < 0 {
			break
		}
		colon += closeQuote
		valueOpen := strings.Index(text[colon:], `"`)
		if valueOpen < 0 {
			break
		}
		valueOpen += colon
		valueClose := strings.Index(text[valueOpen+1:], `"`)
		if valueClose < 0 {
			break
		}
		valueClose += valueOpen + 1
		pins[key] = text[valueOpen+1 : valueClose]
		index = valueClose + 1
	}
	return pins
}

// PinText is the pin file's text for a set of tools.
func PinText(entries []Entry) string {
	lines := []string{"{"}
	for index, entry := range entries {
		comma := ","
		if index == len(entries)-1 {
			comma = ""
		}
		lines = append(lines, `  "`+entry.Name+`": "`+Digest(entry.Schema)+`"`+comma)
	}
	lines = append(lines, "}")
	return strings.Join(lines, "\n") + "\n"
}

// WritePin writes the pin file: one hash per tool.
func WritePin(root string, entries []Entry) error {
	dir := filepath.Join(root, SchemasRoot)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(dir, PinName), []byte(PinText(entries)), 0o644)
}

// ---------------------------------------------------------------- the legacy form

// extractLegacy reads the class name and the schema of the legacy literal in
// one file.
//
// The legacy form is a static JsonDocument field built from a raw string.
// It is gone from the tree -- the migration rewrote every tool -- and the
// reader is kept so that a file that still carries one is read the way the
// pin was written from it.
func extractLegacy(path string, text string) (*Entry, error) {
	body, _, _, ok := legacyMatch(text)
	if !ok {
		return nil, nil
	}
	classMatch := toolClassRe.FindStringSubmatch(text)
	if classMatch == nil {
		return nil, failuref("%s: a legacy schema without a tool class", path)
	}
	dedented, err := dedentRawString(body)
	if err != nil {
		return nil, failuref("%s: %s", path, err.Error())
	}
	schema, err := ParseJSON(dedented)
	if err != nil {
		return nil, failuref("%s: the legacy schema is not JSON: %s", path, err.Error())
	}
	return &Entry{Name: classMatch[2], Schema: schema}, nil
}

// legacyMatch returns the text of the legacy literal, if the file carries
// one, and the span of the lines it occupies.
//
// The shape is the one the migration wrote:
//
//	/// <summary>
//
// /	///     The tool's parameter schema. ...
// /	/// </summary>
//
//	private static readonly JsonDocument Schema = JsonDocument.Parse("""
//	...the schema...
//	""");
//	/// <inheritdoc />
//	public JsonDocument ParameterSchema => Schema;
//
// The blank line after the property is part of the shape: the python reader
// matched the whole block including it, and a file without it is not the
// form the pin was written from.
func legacyMatch(text string) (body string, start int, end int, ok bool) {
	lines := strings.Split(text, "\n")
	for i := 0; i+8 < len(lines); i++ {
		if !isLine(lines[i], "/// <summary>") {
			continue
		}
		if !strings.HasPrefix(trimLeftSpaceTab(lines[i+1]), "///     The tool's parameter schema.") {
			continue
		}
		j := i + 2
		for j < len(lines) && strings.HasPrefix(trimLeftSpaceTab(lines[j]), "///") {
			j++
		}
		if j >= len(lines) || !isLine(lines[j], "/// </summary>") {
			continue
		}
		j++
		if j >= len(lines) || !isLine(lines[j], `private static readonly JsonDocument Schema = JsonDocument.Parse("""`) {
			continue
		}
		first := j + 1
		closing := -1
		for k := first; k < len(lines); k++ {
			if isLine(lines[k], `""");`) {
				closing = k
				break
			}
		}
		if closing < 0 || closing+3 >= len(lines) {
			continue
		}
		if !isLine(lines[closing+1], "/// <inheritdoc />") {
			continue
		}
		if !isLine(lines[closing+2], "public JsonDocument ParameterSchema => Schema;") {
			continue
		}
		if strings.TrimSpace(lines[closing+3]) != "" {
			continue
		}
		return strings.Join(lines[first:closing+1], "\n"), i, closing + 2, true
	}
	return "", 0, 0, false
}

// isLine reports whether a line is exactly the given text, up to its
// leading spaces and tabs.
func isLine(line string, text string) bool {
	return trimLeftSpaceTab(line) == text
}

func trimLeftSpaceTab(line string) string {
	return strings.TrimLeft(line, " \t")
}

// dedentRawString returns the text of a C# raw string literal, with the
// common indentation gone.
//
// A raw string literal strips the indentation of its closing delimiter from
// every line. The legacy literals are indented to the column of the code
// that holds them, so the text the runtime saw is the relative indentation
// only.
func dedentRawString(body string) (string, error) {
	lines := strings.Split(body, "\n")
	if len(lines) == 0 {
		return "", nil
	}
	closing := lines[len(lines)-1]
	indent := len(closing) - len(strings.TrimLeft(closing, " "))
	out := make([]string, 0, len(lines)-1)
	for _, line := range lines[:len(lines)-1] {
		if len(line) < indent {
			return "", failuref("a raw string line is indented less than its closing delimiter: %q", line)
		}
		if strings.TrimSpace(line[:indent]) != "" {
			return "", failuref("a raw string line is indented less than its closing delimiter: %q", line)
		}
		out = append(out, line[indent:])
	}
	return strings.Join(out, "\n"), nil
}

// ---------------------------------------------------------------- the declaration

// param is one [ToolParam(...)] declaration.
type param struct {
	name        string
	typ         string
	description *string
	enum        []string
	required    bool
	itemsOf     *string
}

// readDeclaration reads the schema a tool file declares, from its attributes.
//
// A file without the [ToolSchema] marker declares nothing, and a file with
// the marker but without a parameter is a refusal: the marker is a claim,
// and a claim with nothing under it is a lie.
func readDeclaration(path string, text string) (*Entry, error) {
	if !strings.Contains(text, schemaMarker) {
		return nil, nil
	}
	classMatch := toolClassRe.FindStringSubmatch(text)
	if classMatch == nil {
		return nil, nil
	}
	params, err := parseParams(text)
	if err != nil {
		return nil, failuref("%s: %s", path, err.Error())
	}
	if len(params) == 0 {
		return nil, failuref("%s: a declared schema without a parameter", path)
	}

	root := NewObject()
	items := map[string]*Value{}
	var itemOrder []string
	var rootRequired []string
	itemRequired := map[string][]string{}

	for _, p := range params {
		if !knownTypes[p.typ] {
			return nil, failuref("%s: parameter '%s' has the unknown type %q", path, p.name, p.typ)
		}
		spec := NewObject()
		spec.Set("type", NewString(p.typ))
		if p.enum != nil {
			values := NewArray()
			for _, value := range p.enum {
				values.Append(NewString(value))
			}
			spec.Set("enum", values)
		}
		if p.description != nil {
			spec.Set("description", NewString(*p.description))
		}

		if p.itemsOf != nil {
			parent := *p.itemsOf
			properties, ok := items[parent]
			if !ok {
				properties = NewObject()
				items[parent] = properties
				itemOrder = append(itemOrder, parent)
			}
			properties.Set(p.name, spec)
			if p.required {
				itemRequired[parent] = append(itemRequired[parent], p.name)
			}
			continue
		}

		root.Set(p.name, spec)
		if p.required {
			rootRequired = append(rootRequired, p.name)
		}
	}

	schema := NewObject()
	schema.Set("type", NewString("object"))
	schema.Set("properties", root)
	if len(rootRequired) > 0 {
		required := NewArray()
		for _, name := range rootRequired {
			required.Append(NewString(name))
		}
		schema.Set("required", required)
	}
	for _, parent := range itemOrder {
		spec := root.Get(parent)
		if spec == nil {
			return nil, failuref("%s: a parameter names the items of %q, which it does not declare", path, parent)
		}
		item := NewObject()
		item.Set("type", NewString("object"))
		item.Set("properties", items[parent])
		if names := itemRequired[parent]; len(names) > 0 {
			required := NewArray()
			for _, name := range names {
				required.Append(NewString(name))
			}
			item.Set("required", required)
		}
		spec.Set("items", item)
	}
	return &Entry{Name: classMatch[2], Schema: schema}, nil
}

// parseParams returns every [ToolParam(...)] declaration in a text.
func parseParams(text string) ([]param, error) {
	var found []param
	index := 0
	for {
		offset := strings.Index(text[index:], "[ToolParam(")
		if offset < 0 {
			return found, nil
		}
		start := offset + index
		arguments, next := scanArguments(text, start+len("[ToolParam("))
		index = next
		parts := splitArguments(arguments)
		if len(parts) < 2 {
			return nil, failuref("a [ToolParam] declaration without a name and a type: %q", arguments)
		}
		p := param{
			name: unescapeCS(quotedBody(parts[0])),
			typ:  unescapeCS(quotedBody(parts[1])),
		}
		rest := strings.Join(parts[2:], " ")
		if match := descriptionRe.FindStringSubmatch(rest); match != nil {
			value := unescapeCS(match[1])
			p.description = &value
		}
		if match := enumRe.FindStringSubmatch(rest); match != nil {
			values := []string{}
			for _, value := range strings.Split(match[1], ",") {
				trimmed := strings.TrimSpace(value)
				if trimmed == "" {
					continue
				}
				values = append(values, unescapeCS(strings.Trim(trimmed, `"`)))
			}
			p.enum = values
		}
		if match := itemsOfRe.FindStringSubmatch(rest); match != nil {
			value := unescapeCS(match[1])
			p.itemsOf = &value
		}
		p.required = requiredRe.MatchString(rest)
		found = append(found, p)
	}
}

// scanArguments returns the argument list of an attribute, from the opening
// parenthesis.
//
// A description may hold a bracket, a quote or a brace, so the scan has to
// know what a string literal is: a regex over the arguments would stop at
// the first bracket inside a description and read the rest of the
// declaration wrongly.
func scanArguments(text string, start int) (string, int) {
	index := start
	depth := 0
	inString := false
	escaped := false
	var out strings.Builder
	for index < len(text) {
		char := text[index]
		if inString {
			out.WriteByte(char)
			switch {
			case escaped:
				escaped = false
			case char == '\\':
				escaped = true
			case char == '"':
				inString = false
			}
			index++
			continue
		}
		if char == '"' {
			inString = true
			out.WriteByte(char)
			index++
			continue
		}
		switch char {
		case '{', '[':
			depth++
		case '}', ']':
			depth--
		case ')':
			if depth == 0 {
				return out.String(), index + 1
			}
		}
		out.WriteByte(char)
		index++
	}
	return out.String(), index
}

// splitArguments splits the comma-separated arguments of an attribute,
// strings kept whole.
func splitArguments(arguments string) []string {
	var parts []string
	var current strings.Builder
	depth := 0
	inString := false
	escaped := false
	for i := 0; i < len(arguments); i++ {
		char := arguments[i]
		if inString {
			current.WriteByte(char)
			switch {
			case escaped:
				escaped = false
			case char == '\\':
				escaped = true
			case char == '"':
				inString = false
			}
			continue
		}
		if char == '"' {
			inString = true
			current.WriteByte(char)
			continue
		}
		switch char {
		case '{', '[':
			depth++
		case '}', ']':
			depth--
		case ',':
			if depth == 0 {
				parts = append(parts, current.String())
				current.Reset()
				continue
			}
		}
		current.WriteByte(char)
	}
	parts = append(parts, current.String())
	return parts
}

// quotedBody strips the surrounding quotes of an argument and its spaces.
func quotedBody(part string) string {
	trimmed := strings.TrimSpace(part)
	if len(trimmed) < 2 {
		return ""
	}
	return trimmed[1 : len(trimmed)-1]
}

// unescapeCS reads a C# string literal body back.
func unescapeCS(value string) string {
	escapes := map[byte]string{
		'n': "\n", 'r': "\r", 't': "\t", '"': `"`, '\\': "\\",
	}
	var out strings.Builder
	for index := 0; index < len(value); index++ {
		char := value[index]
		if char == '\\' && index+1 < len(value) {
			if replacement, ok := escapes[value[index+1]]; ok {
				out.WriteString(replacement)
				index++
				continue
			}
		}
		out.WriteByte(char)
	}
	return out.String()
}

// escapeCS writes a C# string literal body for a value that may hold quotes.
func escapeCS(value string) string {
	var out strings.Builder
	for i := 0; i < len(value); i++ {
		char := value[i]
		switch char {
		case '\\':
			out.WriteString(`\\`)
		case '"':
			out.WriteString(`\"`)
		case '\n':
			out.WriteString(`\n`)
		case '\r':
			out.WriteString(`\r`)
		case '\t':
			out.WriteString(`\t`)
		default:
			out.WriteByte(char)
		}
	}
	return out.String()
}

// relativeTo returns a path relative to the root, with '/' separators.
func relativeTo(root string, path string) string {
	relative, err := filepath.Rel(root, path)
	if err != nil {
		return filepath.ToSlash(path)
	}
	return filepath.ToSlash(relative)
}
