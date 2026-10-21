package resultchains

// json.go -- the JSON the gate prints, in python's `json.dump(indent=2)`
// shape.
//
// The gate's JSON is read by a human and by the baseline file, and the
// baseline file is compared byte for byte by the next run of the gate, so
// the shape is not free: python's `indent=2` puts a comma at the end of a
// line and nothing after it, indents by two spaces per level, and leaves
// no space after the colon. Go's `encoding/json` with `SetIndent` writes
// the same shape, but it SORTS the keys of a map, and the python original
// wrote the keys in the order the census first saw them.

import (
	"fmt"
	"sort"
	"strings"
)

// jsonField is one member of a JSON object.
type jsonField struct {
	key   string
	value string
}

// jsonNumber renders an integer.
func jsonNumber(n int) string {
	return fmt.Sprintf("%d", n)
}

// jsonString renders a string, the way python's json does with
// ensure_ascii=True: the ASCII escapes, and every other character as a
// \uXXXX escape.
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

// jsonObject renders an object with the given members, at the given
// indentation level.
//
// `sorted` is python's `sort_keys=True`: the members are ordered by their
// key instead of by the order they were added.
func jsonObject(fields []jsonField, sorted bool) string {
	return jsonObjectAt(fields, sorted, 0)
}

// jsonObjectAt renders an object at a given indentation level.
func jsonObjectAt(fields []jsonField, sorted bool, level int) string {
	if sorted {
		sortedFields := append([]jsonField{}, fields...)
		sort.SliceStable(sortedFields, func(i, j int) bool {
			return sortedFields[i].key < sortedFields[j].key
		})
		fields = sortedFields
	}
	if len(fields) == 0 {
		return "{}"
	}
	indent := strings.Repeat("  ", level+1)
	var b strings.Builder
	b.WriteString("{\n")
	for i, field := range fields {
		b.WriteString(indent)
		b.WriteString(jsonString(field.key))
		b.WriteString(": ")
		b.WriteString(field.value)
		if i < len(fields)-1 {
			b.WriteString(",")
		}
		b.WriteString("\n")
	}
	b.WriteString(strings.Repeat("  ", level))
	b.WriteString("}")
	return b.String()
}

// jsonCounts renders an object of counts, in the given order, or sorted
// when the caller asks for it.
func jsonCounts(counts map[string]int, order []string, sorted bool) string {
	keys := order
	if sorted {
		keys = append([]string{}, order...)
		sort.Strings(keys)
	}
	fields := make([]jsonField, 0, len(keys))
	for _, key := range keys {
		fields = append(fields, jsonField{key, jsonNumber(counts[key])})
	}
	return jsonObjectAt(fields, false, 1)
}

// jsonArray renders an array of rendered values at the given indentation
// level.
func jsonArray(values []string) string {
	return jsonArrayAt(values, 1)
}

// jsonArrayAt renders an array at a given indentation level.
func jsonArrayAt(values []string, level int) string {
	if len(values) == 0 {
		return "[]"
	}
	indent := strings.Repeat("  ", level+1)
	var b strings.Builder
	b.WriteString("[\n")
	for i, value := range values {
		b.WriteString(indent)
		b.WriteString(value)
		if i < len(values)-1 {
			b.WriteString(",")
		}
		b.WriteString("\n")
	}
	b.WriteString(strings.Repeat("  ", level))
	b.WriteString("]")
	return b.String()
}
