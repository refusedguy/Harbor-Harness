package gatekit

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"
)

// ReadJSONObject reads a JSON object into a map.
//
// The numbers are kept as json.Number, NOT as float64: a measurement file
// holds both integers ("calls": 12) and floats ("avg_busy_cores": 3.0), and
// the python originals printed both the way python printed them — "12" and
// "3.0". A float64 would print both as "12" and "3".
func ReadJSONObject(path string) (map[string]any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return ParseJSONObject(data)
}

// ParseJSONObject reads a JSON object out of a byte slice.
func ParseJSONObject(data []byte) (map[string]any, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	var value any
	if err := decoder.Decode(&value); err != nil {
		return nil, err
	}
	object, ok := value.(map[string]any)
	if !ok {
		return nil, fmt.Errorf("not a JSON object")
	}
	return object, nil
}

// JSONObject is a JSON object that keeps the ORDER of its keys.
//
// The python originals wrote their results with json.dump, which keeps the
// insertion order of the dict, and the artifacts are read by humans and by
// diffs. A Go map has no order, so the order is carried here.
type JSONObject struct {
	Keys   []string
	Values map[string]any
}

// NewJSONObject is an empty object.
func NewJSONObject() *JSONObject {
	return &JSONObject{Values: map[string]any{}}
}

// Set adds or replaces a key.
func (o *JSONObject) Set(key string, value any) {
	if _, ok := o.Values[key]; !ok {
		o.Keys = append(o.Keys, key)
	}
	o.Values[key] = value
}

// Get returns a value of the object.
func (o *JSONObject) Get(key string) any {
	return o.Values[key]
}

// JSONMap returns a nested object, or nil.
func JSONMap(object map[string]any, key string) map[string]any {
	if object == nil {
		return nil
	}
	value, ok := object[key].(map[string]any)
	if !ok {
		return nil
	}
	return value
}

// JSONList returns a nested array, or nil.
func JSONList(object map[string]any, key string) []any {
	if object == nil {
		return nil
	}
	value, ok := object[key].([]any)
	if !ok {
		return nil
	}
	return value
}

// JSONNumber returns a nested number, and whether it was one.
func JSONNumber(object map[string]any, key string) (float64, bool) {
	if object == nil {
		return 0, false
	}
	switch typed := object[key].(type) {
	case json.Number:
		value, err := typed.Float64()
		if err != nil {
			return 0, false
		}
		return value, true
	case float64:
		return typed, true
	case int:
		return float64(typed), true
	}
	return 0, false
}

// JSONInt returns a nested number as an integer, and whether it was one.
func JSONInt(object map[string]any, key string) (int, bool) {
	if object == nil {
		return 0, false
	}
	switch typed := object[key].(type) {
	case json.Number:
		value, err := typed.Int64()
		if err != nil {
			return 0, false
		}
		return int(value), true
	case float64:
		return int(typed), true
	case int:
		return typed, true
	}
	return 0, false
}

// JSONObjectChild returns a nested object of an ordered object.
func JSONObjectChild(object *JSONObject, key string) *JSONObject {
	if object == nil {
		return nil
	}
	child, _ := object.Get(key).(*JSONObject)
	return child
}

// JSONObjectText renders a value of an ordered object the way python's str()
// rendered it.
func JSONObjectText(object *JSONObject, key string) string {
	if object == nil {
		return "None"
	}
	switch typed := object.Get(key).(type) {
	case nil:
		return "None"
	case bool:
		if typed {
			return "True"
		}
		return "False"
	case string:
		return typed
	case int:
		return strconv.Itoa(typed)
	case float64:
		return PyFloat(typed)
	case json.Number:
		text := typed.String()
		if strings.ContainsAny(text, ".eE") {
			if number, err := typed.Float64(); err == nil {
				return PyFloat(number)
			}
		}
		return text
	}
	return "None"
}

// RawValue returns a nested value as it was read, and whether it was there.
func RawValue(object map[string]any, key string) (any, bool) {
	if object == nil {
		return nil, false
	}
	value, ok := object[key]
	return value, ok
}

// ParseFloat reads a number the way python's float() read it, with the
// comparison marks of a measurement stripped first.
func ParseFloat(text string) (float64, error) {
	trimmed := strings.TrimLeft(text, "<>")
	return strconv.ParseFloat(strings.TrimSpace(trimmed), 64)
}

// JSONText returns a nested string, and whether it was one.
func JSONText(object map[string]any, key string) (string, bool) {
	if object == nil {
		return "", false
	}
	value, ok := object[key].(string)
	return value, ok
}

// JSONTruthy is the python truth of a nested value.
//
// The python originals wrote `data.get("x") or 0` and `if pf.get("available"):`
// — the truth of a value, not its presence. A zero and a false are falsy, a
// missing key is falsy, and so is an empty string.
func JSONTruthy(object map[string]any, key string) bool {
	if object == nil {
		return false
	}
	value, ok := object[key]
	if !ok {
		return false
	}
	switch typed := value.(type) {
	case nil:
		return false
	case bool:
		return typed
	case string:
		return typed != ""
	case json.Number:
		number, err := typed.Float64()
		if err != nil {
			return false
		}
		return number != 0
	case float64:
		return typed != 0
	case int:
		return typed != 0
	case []any:
		return len(typed) > 0
	case map[string]any:
		return len(typed) > 0
	}
	return true
}

// JSONTextOf renders a nested value the way python's str() rendered it.
//
// A number keeps the text it was written with, because that is what python
// read it back as: an int prints as "12" and a float prints as "3.0".
func JSONTextOf(object map[string]any, key string) string {
	if object == nil {
		return "None"
	}
	value, ok := object[key]
	if !ok {
		return "None"
	}
	switch typed := value.(type) {
	case nil:
		return "None"
	case bool:
		if typed {
			return "True"
		}
		return "False"
	case string:
		return typed
	case json.Number:
		// Python reads an integer as an int and a decimal as a float, and
		// prints them the way each prints itself: "12" and "12.0".
		text := typed.String()
		if strings.ContainsAny(text, ".eE") {
			if number, err := typed.Float64(); err == nil {
				return PyFloat(number)
			}
		}
		return text
	case float64:
		return PyFloat(typed)
	case int:
		return strconv.Itoa(typed)
	case []any:
		return "[]"
	case map[string]any:
		return "{}"
	}
	return "None"
}

// PyFloat renders a float the way python's repr() rendered it.
//
// The difference that matters: python prints 3.0 as "3.0" and 3 as "3", and
// the measurement tables are read by humans who notice.
func PyFloat(value float64) string {
	text := strconv.FormatFloat(value, 'g', -1, 64)
	if !strings.ContainsAny(text, ".eE") {
		text += ".0"
	}
	return text
}

// WriteJSON writes a value the way python's json.dump(indent=2) wrote it.
//
// The order of the keys is the order of the JSONObject, the strings are
// escaped with ensure_ascii (python's default), and the floats are python's
// repr.
func WriteJSON(value any, indent int) string {
	var out strings.Builder
	writeJSONValue(&out, value, indent)
	return out.String()
}

// writeJSONValue writes one value.
func writeJSONValue(out *strings.Builder, value any, indent int) {
	pad := strings.Repeat(" ", indent)
	inner := strings.Repeat(" ", indent+2)
	switch typed := value.(type) {
	case nil:
		out.WriteString("null")
	case bool:
		if typed {
			out.WriteString("true")
		} else {
			out.WriteString("false")
		}
	case string:
		out.WriteString(QuoteJSONString(typed))
	case int:
		out.WriteString(strconv.Itoa(typed))
	case int64:
		out.WriteString(strconv.FormatInt(typed, 10))
	case float64:
		out.WriteString(PyFloat(typed))
	case json.Number:
		out.WriteString(typed.String())
	case *JSONObject:
		if typed == nil || len(typed.Keys) == 0 {
			out.WriteString("{}")
			return
		}
		out.WriteString("{\n")
		for index, key := range typed.Keys {
			out.WriteString(inner)
			out.WriteString(QuoteJSONString(key))
			out.WriteString(": ")
			writeJSONValue(out, typed.Values[key], indent+2)
			if index < len(typed.Keys)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("}")
	case map[string]any:
		keys := make([]string, 0, len(typed))
		for key := range typed {
			keys = append(keys, key)
		}
		sort.Strings(keys)
		if len(keys) == 0 {
			out.WriteString("{}")
			return
		}
		out.WriteString("{\n")
		for index, key := range keys {
			out.WriteString(inner)
			out.WriteString(QuoteJSONString(key))
			out.WriteString(": ")
			writeJSONValue(out, typed[key], indent+2)
			if index < len(keys)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("}")
	case []any:
		if len(typed) == 0 {
			out.WriteString("[]")
			return
		}
		out.WriteString("[\n")
		for index, item := range typed {
			out.WriteString(inner)
			writeJSONValue(out, item, indent+2)
			if index < len(typed)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("]")
	case []string:
		if len(typed) == 0 {
			out.WriteString("[]")
			return
		}
		out.WriteString("[\n")
		for index, item := range typed {
			out.WriteString(inner)
			out.WriteString(QuoteJSONString(item))
			if index < len(typed)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("]")
	case []int:
		if len(typed) == 0 {
			out.WriteString("[]")
			return
		}
		out.WriteString("[\n")
		for index, item := range typed {
			out.WriteString(inner)
			out.WriteString(strconv.Itoa(item))
			if index < len(typed)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("]")
	case []float64:
		if len(typed) == 0 {
			out.WriteString("[]")
			return
		}
		out.WriteString("[\n")
		for index, item := range typed {
			out.WriteString(inner)
			out.WriteString(PyFloat(item))
			if index < len(typed)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("]")
	case []*JSONObject:
		if len(typed) == 0 {
			out.WriteString("[]")
			return
		}
		out.WriteString("[\n")
		for index, item := range typed {
			out.WriteString(inner)
			writeJSONValue(out, item, indent+2)
			if index < len(typed)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("]")
	case [][]float64:
		if len(typed) == 0 {
			out.WriteString("[]")
			return
		}
		out.WriteString("[\n")
		for index, item := range typed {
			out.WriteString(inner)
			out.WriteString("[")
			for position, value := range item {
				if position > 0 {
					out.WriteString(", ")
				}
				out.WriteString(PyFloat(value))
			}
			out.WriteString("]")
			if index < len(typed)-1 {
				out.WriteString(",")
			}
			out.WriteString("\n")
		}
		out.WriteString(pad)
		out.WriteString("]")
	default:
		out.WriteString("null")
	}
}

// QuoteJSONString renders a string the way python's json module wrote it.
//
// ensure_ascii is on: a character outside ASCII becomes a \\uXXXX escape.
func QuoteJSONString(text string) string {
	var out strings.Builder
	out.WriteByte('"')
	for _, r := range text {
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
				out.WriteString(fmt.Sprintf(`\u%04x`, r))
				continue
			}
			if r < 0x7f {
				out.WriteRune(r)
				continue
			}
			if r > 0xffff {
				high := 0xd800 + ((r - 0x10000) >> 10)
				low := 0xdc00 + ((r - 0x10000) & 0x3ff)
				out.WriteString(fmt.Sprintf(`\u%04x\u%04x`, high, low))
				continue
			}
			out.WriteString(fmt.Sprintf(`\u%04x`, r))
		}
	}
	out.WriteByte('"')
	return out.String()
}
