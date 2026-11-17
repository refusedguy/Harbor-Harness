package toolschema

// migrate.go -- the one-shot migration from the legacy literal to the
// [ToolParam] declaration.
//
// The migration has run: no tool carries the legacy literal any more. The
// code is kept because it is the WRITER of the pin -- a file that still
// carries the old form is rewritten here, and the pin is written from the
// same model the gate reads it with, so the two cannot drift apart.

import (
	"fmt"
	"os"
	"strings"
)

// attributeLines returns the [ToolParam] lines that declare one schema.
func attributeLines(schema *Value) ([]string, error) {
	properties := schema.Get("properties")
	if properties == nil {
		properties = NewObject()
	}
	var required []string
	if list := schema.Get("required"); list != nil {
		for _, element := range list.Arr {
			required = append(required, element.Str)
		}
	}

	var lines []string
	for i, name := range properties.Keys {
		spec := properties.Vals[i]
		typ := "string"
		if value := spec.Get("type"); value != nil {
			typ = value.Str
		}
		if !knownTypes[typ] {
			return nil, failuref("property '%s' has the unknown type %q", name, typ)
		}
		args := []string{`"` + escapeCS(name) + `"`, `"` + escapeCS(typ) + `"`}
		if values := spec.Get("enum"); values != nil {
			quoted := make([]string, 0, len(values.Arr))
			for _, value := range values.Arr {
				quoted = append(quoted, `"`+escapeCS(value.Str)+`"`)
			}
			args = append(args, "Enum = new[] { "+strings.Join(quoted, ", ")+" }")
		}
		if description := spec.Get("description"); description != nil {
			args = append(args, `Description = "`+escapeCS(description.Str)+`"`)
		}
		if contains(required, name) {
			args = append(args, "Required = true")
		}
		lines = append(lines, "[ToolParam("+strings.Join(args, ", ")+")]")

		items := spec.Get("items")
		if items == nil {
			continue
		}
		if typ != "array" {
			return nil, failuref("property '%s' has items but is not an array", name)
		}
		var itemRequired []string
		if list := items.Get("required"); list != nil {
			for _, element := range list.Arr {
				itemRequired = append(itemRequired, element.Str)
			}
		}
		itemProperties := items.Get("properties")
		if itemProperties == nil {
			continue
		}
		for j, itemName := range itemProperties.Keys {
			itemSpec := itemProperties.Vals[j]
			itemType := "string"
			if value := itemSpec.Get("type"); value != nil {
				itemType = value.Str
			}
			itemArgs := []string{
				`"` + escapeCS(itemName) + `"`,
				`"` + escapeCS(itemType) + `"`,
				`ItemsOf = "` + escapeCS(name) + `"`,
			}
			if description := itemSpec.Get("description"); description != nil {
				itemArgs = append(itemArgs, `Description = "`+escapeCS(description.Str)+`"`)
			}
			if contains(itemRequired, itemName) {
				itemArgs = append(itemArgs, "Required = true")
			}
			lines = append(lines, "[ToolParam("+strings.Join(itemArgs, ", ")+")]")
		}
	}
	return lines, nil
}

func contains(list []string, value string) bool {
	for _, item := range list {
		if item == value {
			return true
		}
	}
	return false
}

// MigrateFile rewrites one tool file: the literal becomes attributes.
//
// It returns the class name and the digest of the schema the file carried.
// The class name is EMPTY when the file carried no legacy literal, which is
// how the caller counts the files the migration rewrote.
func MigrateFile(path string) (string, string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", "", err
	}
	text := string(data)

	body, start, end, ok := legacyMatch(text)
	if !ok {
		return "", "", nil
	}
	classMatch := toolClassRe.FindStringSubmatch(text)
	if classMatch == nil {
		return "", "", failuref("%s: a legacy schema without a tool class", path)
	}
	className := classMatch[2]

	dedented, err := dedentRawString(body)
	if err != nil {
		return "", "", failuref("%s: %s", path, err.Error())
	}
	schema, err := ParseJSON(dedented)
	if err != nil {
		return "", "", failuref("%s: the legacy schema is not JSON: %s", path, err.Error())
	}
	lines, err := attributeLines(schema)
	if err != nil {
		return "", "", failuref("%s: %s", path, err.Error())
	}

	original := classMatch[0]
	classDecl := original
	if !strings.Contains(classDecl, "partial") {
		classDecl = strings.Replace(classDecl, "public sealed class", "public sealed partial class", 1)
	}
	text = strings.Replace(text, original, classDecl, 1)

	marker := schemaMarker + "\n"
	for _, line := range lines {
		marker += line + "\n"
	}
	text = strings.Replace(text, classDecl, marker+classDecl, 1)

	// The literal and the property it fed go away.
	text = removeLines(text, start, end+1)

	// A tool that lives in another namespace needs the attributes in scope.
	if !strings.Contains(text, "namespace Harbor.Tools.Builtin;") &&
		!strings.Contains(text, "using Harbor.Tools.Builtin;") {
		text = insertUsing(text, "using Harbor.Tools.Builtin;")
	}

	if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
		return "", "", err
	}
	return className, Digest(schema), nil
}

// removeLines drops the lines from first to last, both included.
func removeLines(text string, first int, last int) string {
	lines := strings.Split(text, "\n")
	if first < 0 || last < first || last >= len(lines) {
		return text
	}
	kept := make([]string, 0, len(lines)-(last-first+1))
	kept = append(kept, lines[:first]...)
	kept = append(kept, lines[last+1:]...)
	return strings.Join(kept, "\n")
}

// insertUsing puts a using directive where the file's other ones say it
// belongs.
func insertUsing(text string, statement string) string {
	lines := strings.Split(text, "\n")
	index := 0
	for position, line := range lines {
		if !strings.HasPrefix(line, "using ") {
			continue
		}
		if line > statement {
			index = position
			break
		}
		index = position + 1
	}
	updated := make([]string, 0, len(lines)+1)
	updated = append(updated, lines[:index]...)
	updated = append(updated, statement)
	updated = append(updated, lines[index:]...)
	return strings.Join(updated, "\n")
}

// Describe renders an entry for a report: the class name and its digest.
func Describe(entry Entry) string {
	return fmt.Sprintf("%s: %s", entry.Name, Digest(entry.Schema))
}
