package testdup

// jsonw.go -- a JSON writer that produces the same bytes as python's
// json.dump(..., indent=2, sort_keys=False).
//
// The baseline is a reviewed file: a reviewer reads its diff, so the bytes
// the gate writes must be the bytes python wrote, down to the order of the
// keys and the escaping of the strings.

import (
	"fmt"
	"strings"
	"unicode/utf16"
)

// jsonNode is a JSON value whose object keeps the order of its keys.
type jsonNode struct {
	// scalar is the text of a scalar value, already in JSON form.
	scalar string
	// isObject and isArray say what the value holds.
	isObject bool
	isArray  bool
	// keys and values are parallel: values[i] is the value of keys[i].
	keys   []string
	values []*jsonNode
}

// jsonObject is an empty JSON object.
func jsonObject() *jsonNode { return &jsonNode{isObject: true} }

// jsonArray is an empty JSON array.
func jsonArray() *jsonNode { return &jsonNode{isArray: true} }

// jsonString is a JSON string.
func jsonString(value string) *jsonNode {
	return &jsonNode{scalar: quoteJSONString(value)}
}

// jsonNumber is a JSON number.
func jsonNumber(value int) *jsonNode {
	return &jsonNode{scalar: itoa(value)}
}

// jsonFloat is a JSON number written the way python writes a float.
func jsonFloat(value float64) *jsonNode {
	return &jsonNode{scalar: pyFloatText(value)}
}

// jsonBool is a JSON boolean.
func jsonBool(value bool) *jsonNode {
	if value {
		return &jsonNode{scalar: "true"}
	}
	return &jsonNode{scalar: "false"}
}

// set puts a key into an object, keeping the order it was set in.
func (n *jsonNode) set(key string, value *jsonNode) {
	n.keys = append(n.keys, key)
	n.values = append(n.values, value)
}

// append adds an element to an array.
func (n *jsonNode) append(value *jsonNode) {
	n.values = append(n.values, value)
}

// render writes the value the way python's json.dump(indent=2) writes it.
func (n *jsonNode) render(depth int) string {
	switch {
	case n.isObject:
		if len(n.keys) == 0 {
			return "{}"
		}
		var out strings.Builder
		out.WriteString("{\n")
		for index, key := range n.keys {
			out.WriteString(strings.Repeat("  ", depth+1))
			out.WriteString(quoteJSONString(key))
			out.WriteString(": ")
			out.WriteString(n.values[index].render(depth + 1))
			if index < len(n.keys)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(strings.Repeat("  ", depth))
		out.WriteString("}")
		return out.String()
	case n.isArray:
		if len(n.values) == 0 {
			return "[]"
		}
		var out strings.Builder
		out.WriteString("[\n")
		for index, value := range n.values {
			out.WriteString(strings.Repeat("  ", depth+1))
			out.WriteString(value.render(depth + 1))
			if index < len(n.values)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(strings.Repeat("  ", depth))
		out.WriteString("]")
		return out.String()
	default:
		return n.scalar
	}
}

// quoteJSONString writes a JSON string literal the way python's json.dump
// writes it: the ASCII is written as it is, and everything above it is
// written as an escape.
func quoteJSONString(value string) string {
	var out strings.Builder
	out.WriteByte('"')
	for _, r := range value {
		switch r {
		case '"':
			out.WriteString(`\"`)
		case '\\':
			out.WriteString(`\\`)
		case '\n':
			out.WriteString(`\n`)
		case '\r':
			out.WriteString(`\r`)
		case '\t':
			out.WriteString(`\t`)
		case '\b':
			out.WriteString(`\b`)
		case '\f':
			out.WriteString(`\f`)
		default:
			switch {
			case r < 0x20:
				fmt.Fprintf(&out, `\u%04x`, r)
			case r < 0x7f:
				out.WriteRune(r)
			case r > 0xffff:
				high, low := utf16.EncodeRune(r)
				fmt.Fprintf(&out, `\u%04x\u%04x`, high, low)
			default:
				fmt.Fprintf(&out, `\u%04x`, r)
			}
		}
	}
	out.WriteByte('"')
	return out.String()
}
