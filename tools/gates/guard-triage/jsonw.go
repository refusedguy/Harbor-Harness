package guardtriage

// jsonw.go -- a JSON writer that produces the same bytes as python's
// json.dumps(..., indent=2, ensure_ascii=False).
//
// The report this gate writes is a machine-readable artifact, and a reviewer
// reads its diff. The bytes must therefore be the bytes the python original
// wrote: the order of the keys, the two-space indentation and the escaping of
// the strings all matter. encoding/json cannot do this -- it sorts map keys,
// escapes HTML by default and writes non-ASCII as UTF-8 only with SetEscapeHTML
// off, but still offers no ordered objects. So the report is built as a small
// tree whose objects keep the order their keys were set in.
//
// The one difference from the ported gates that write python JSON is
// ensure_ascii: guard-triage.py passes False, so non-ASCII (the reasons in the
// registry carry em dashes) is written as UTF-8, not as \u escapes.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

	"harbor.local/gates/internal/pyfmt"
)

// jnode is one JSON value. An object keeps the order its keys were set in.
type jnode struct {
	scalar   string // already in JSON form, for scalars
	isObject bool
	isArray  bool
	keys     []string
	values   []*jnode
}

func jobject() *jnode { return &jnode{isObject: true} }
func jarray() *jnode  { return &jnode{isArray: true} }

func jstring(value string) *jnode { return &jnode{scalar: quotePyJSON(value)} }
func jint(value int) *jnode       { return &jnode{scalar: strconv.Itoa(value)} }
func jfloat(value float64) *jnode { return &jnode{scalar: pyfmt.Repr(value)} }
func jbool(value bool) *jnode {
	if value {
		return &jnode{scalar: "true"}
	}
	return &jnode{scalar: "false"}
}
func jnull() *jnode { return &jnode{scalar: "null"} }
func jraw(text string) *jnode {
	return &jnode{scalar: text}
}

// set puts a key into an object, keeping the order it was set in.
func (n *jnode) set(key string, value *jnode) {
	n.keys = append(n.keys, key)
	n.values = append(n.values, value)
}

// add appends an element to an array.
func (n *jnode) add(value *jnode) {
	n.values = append(n.values, value)
}

// render writes the value the way python's json.dumps(indent=2) writes it.
func (n *jnode) render(depth int) string {
	switch {
	case n.isObject:
		if len(n.keys) == 0 {
			return "{}"
		}
		var out strings.Builder
		out.WriteString("{\n")
		for i, key := range n.keys {
			out.WriteString(strings.Repeat("  ", depth+1))
			out.WriteString(quotePyJSON(key))
			out.WriteString(": ")
			out.WriteString(n.values[i].render(depth + 1))
			if i < len(n.keys)-1 {
				out.WriteByte(',')
			}
			out.WriteByte('\n')
		}
		out.WriteString(strings.Repeat("  ", depth))
		out.WriteByte('}')
		return out.String()
	case n.isArray:
		if len(n.values) == 0 {
			return "[]"
		}
		var out strings.Builder
		out.WriteString("[\n")
		for i, value := range n.values {
			out.WriteString(strings.Repeat("  ", depth+1))
			out.WriteString(value.render(depth + 1))
			if i < len(n.values)-1 {
				out.WriteByte(',')
			}
			out.WriteByte('\n')
		}
		out.WriteString(strings.Repeat("  ", depth))
		out.WriteByte(']')
		return out.String()
	default:
		return n.scalar
	}
}

// quotePyJSON writes a JSON string the way python's json.dumps does with
// ensure_ascii=False: the ASCII is written as it is, control characters get
// the short escapes (or \u00xx), and everything above ASCII is written as
// UTF-8 rather than escaped.
func quotePyJSON(value string) string {
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
			if r < 0x20 {
				fmt.Fprintf(&out, `\u%04x`, r)
			} else {
				out.WriteRune(r)
			}
		}
	}
	out.WriteByte('"')
	return out.String()
}

// parseOrderedJSON reads arbitrary JSON (the --runs file) into a jnode tree,
// keeping the order of object keys. Go's decoding into map[string]any would
// lose that order, and python's json.dumps preserves it.
func parseOrderedJSON(data []byte) (*jnode, error) {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	tok, err := dec.Token()
	if err != nil {
		return nil, err
	}
	return orderedValue(dec, tok)
}

func orderedValue(dec *json.Decoder, tok json.Token) (*jnode, error) {
	switch value := tok.(type) {
	case json.Delim:
		switch value {
		case '{':
			obj := jobject()
			for dec.More() {
				keyToken, err := dec.Token()
				if err != nil {
					return nil, err
				}
				key, ok := keyToken.(string)
				if !ok {
					return nil, fmt.Errorf("a JSON object key is not a string")
				}
				childToken, err := dec.Token()
				if err != nil {
					return nil, err
				}
				child, err := orderedValue(dec, childToken)
				if err != nil {
					return nil, err
				}
				obj.set(key, child)
			}
			if _, err := dec.Token(); err != nil {
				return nil, err
			}
			return obj, nil
		case '[':
			arr := jarray()
			for dec.More() {
				childToken, err := dec.Token()
				if err != nil {
					return nil, err
				}
				child, err := orderedValue(dec, childToken)
				if err != nil {
					return nil, err
				}
				arr.add(child)
			}
			if _, err := dec.Token(); err != nil {
				return nil, err
			}
			return arr, nil
		}
	case string:
		return jstring(value), nil
	case json.Number:
		text := value.String()
		if integer, err := strconv.ParseInt(text, 10, 64); err == nil {
			return jint(int(integer)), nil
		}
		if number, err := strconv.ParseFloat(text, 64); err == nil {
			return jfloat(number), nil
		}
		return jraw(text), nil
	case bool:
		return jbool(value), nil
	case nil:
		return jnull(), nil
	}
	return nil, fmt.Errorf("unexpected JSON token %v", tok)
}
