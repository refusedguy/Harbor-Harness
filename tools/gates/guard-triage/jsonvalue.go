package guardtriage

// jsonvalue.go -- a JSON value that keeps the ORDER of its members.
//
// The report embeds the metadata of the runs it read, and that metadata
// comes from a JSON file a human wrote. Python's json keeps the order the
// file had; Go's `map[string]any` does not, and `encoding/json` sorts the
// keys of a map when it writes one. The report is an artifact a person
// reads, so the order is part of what they read.
//
// The number is kept as the LITERAL it was written as: python prints the
// int 5 as `5` and the float 5.0 as `5.0`, and a re-serialization that
// turned one into the other would change what the artifact says.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
)

// jsonValue is a JSON value.
type jsonValue struct {
	kind byte // 'n' null, 'b' bool, '#' number, 's' string, 'a' array, 'o' object
	b    bool
	num  json.Number
	str  string
	arr  []jsonValue
	obj  []jsonMember
}

// jsonMember is one member of a JSON object.
type jsonMember struct {
	key   string
	value jsonValue
}

// jsonNull is the null value.
func jsonNull() jsonValue { return jsonValue{kind: 'n'} }

// jsonBool is a boolean value.
func jsonBool(b bool) jsonValue { return jsonValue{kind: 'b', b: b} }

// jsonNumber is a number value.
func jsonNumber(n int) jsonValue {
	return jsonValue{kind: '#', num: json.Number(strconv.Itoa(n))}
}

// jsonString is a string value.
func jsonString(s string) jsonValue { return jsonValue{kind: 's', str: s} }

// jsonArray is an array value.
func jsonArray(items []jsonValue) jsonValue {
	return jsonValue{kind: 'a', arr: items}
}

// jsonObject is an object value; the members keep the order they were
// added in.
func jsonObject(members []jsonMember) jsonValue {
	return jsonValue{kind: 'o', obj: members}
}

// jsonMemberOf is one member of an object.
func jsonMemberOf(key string, value jsonValue) jsonMember {
	return jsonMember{key: key, value: value}
}

// jsonIntOf is a number value from an int.
func jsonIntOf(n int) jsonValue { return jsonNumber(n) }

// parseJSON reads a JSON document into an ordered value.
func parseJSON(data []byte) (jsonValue, error) {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	value, err := decodeJSONValue(dec)
	if err != nil {
		return jsonValue{}, err
	}
	return value, nil
}

// decodeJSONValue reads one value from the decoder.
func decodeJSONValue(dec *json.Decoder) (jsonValue, error) {
	tok, err := dec.Token()
	if err != nil {
		return jsonValue{}, err
	}
	return decodeJSONFromToken(dec, tok)
}

// decodeJSONFromToken reads the value that starts with the token.
func decodeJSONFromToken(dec *json.Decoder, tok json.Token) (jsonValue, error) {
	switch t := tok.(type) {
	case json.Delim:
		switch t {
		case '{':
			value := jsonValue{kind: 'o'}
			for {
				keyTok, err := dec.Token()
				if err != nil {
					return value, err
				}
				if delim, ok := keyTok.(json.Delim); ok && delim == '}' {
					return value, nil
				}
				key, ok := keyTok.(string)
				if !ok {
					return value, fmt.Errorf("a JSON object key is not a string")
				}
				item, err := decodeJSONValue(dec)
				if err != nil {
					return value, err
				}
				value.obj = append(value.obj, jsonMember{key: key, value: item})
			}
		case '[':
			value := jsonValue{kind: 'a'}
			for {
				itemTok, err := dec.Token()
				if err != nil {
					return value, err
				}
				if delim, ok := itemTok.(json.Delim); ok && delim == ']' {
					return value, nil
				}
				item, err := decodeJSONFromToken(dec, itemTok)
				if err != nil {
					return value, err
				}
				value.arr = append(value.arr, item)
			}
		}
		return jsonValue{}, fmt.Errorf("unexpected delimiter %v", t)
	case bool:
		return jsonBool(t), nil
	case json.Number:
		return jsonValue{kind: '#', num: t}, nil
	case string:
		return jsonString(t), nil
	case nil:
		return jsonNull(), nil
	}
	return jsonValue{}, fmt.Errorf("unexpected token")
}

// renderJSON writes the value the way python's `json.dumps(indent=2,
// ensure_ascii=False)` does.
func renderJSON(value jsonValue) string {
	var b strings.Builder
	renderJSONValue(&b, value, 0)
	return b.String()
}

// renderJSONValue writes one value at the given indentation level.
func renderJSONValue(b *strings.Builder, value jsonValue, level int) {
	switch value.kind {
	case 'n':
		b.WriteString("null")
	case 0:
		// The zero value is an empty object: python's default for the
		// `run` metadata of a firing is `{}`, not null.
		b.WriteString("{}")
	case 'b':
		if value.b {
			b.WriteString("true")
		} else {
			b.WriteString("false")
		}
	case '#':
		b.WriteString(value.num.String())
	case 's':
		b.WriteString(renderJSONString(value.str))
	case 'a':
		if len(value.arr) == 0 {
			b.WriteString("[]")
			return
		}
		indent := strings.Repeat("  ", level+1)
		b.WriteString("[\n")
		for i, item := range value.arr {
			b.WriteString(indent)
			renderJSONValue(b, item, level+1)
			if i < len(value.arr)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString(strings.Repeat("  ", level))
		b.WriteString("]")
	case 'o':
		if len(value.obj) == 0 {
			b.WriteString("{}")
			return
		}
		indent := strings.Repeat("  ", level+1)
		b.WriteString("{\n")
		for i, member := range value.obj {
			b.WriteString(indent)
			b.WriteString(renderJSONString(member.key))
			b.WriteString(": ")
			renderJSONValue(b, member.value, level+1)
			if i < len(value.obj)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString(strings.Repeat("  ", level))
		b.WriteString("}")
	}
}

// renderJSONString writes a string the way python does with
// ensure_ascii=False: the escapes for the characters that have them, the
// \uXXXX form for the other control characters, and every other character
// as it is.
func renderJSONString(s string) string {
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
			if r < 0x20 {
				b.WriteString(fmt.Sprintf("\\u%04x", r))
				continue
			}
			b.WriteRune(r)
		}
	}
	b.WriteByte('"')
	return b.String()
}
