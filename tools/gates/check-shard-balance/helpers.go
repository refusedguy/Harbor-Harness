package shardbalance

// helpers.go -- the small shared pieces: parsing, sorting, formatting.
//
// They are here rather than in the gate because the self-test needs the same
// ones, and a self-test that used a DIFFERENT reader than the gate would
// prove nothing about the gate.

import (
	"encoding/json"
	"math"
	"os"
	"sort"
	"strconv"

	"harbor.local/gates/internal/pycompat"
)

// isFile reports whether the path is an existing regular file.
func isFile(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

// sortedKeys returns a map's keys in sorted order.
//
// The python originals iterated `sorted(dict)`, and the order matters: the
// findings come out in a fixed order, and a self-test compares them.
func sortedKeys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// jsonString renders a JSON value the way python's str() would.
func jsonString(value any) string {
	switch v := value.(type) {
	case nil:
		return "None"
	case bool:
		if v {
			return "True"
		}
		return "False"
	case string:
		return v
	case float64:
		return jsonNumber(v)
	default:
		data, err := json.Marshal(v)
		if err != nil {
			return ""
		}
		return string(data)
	}
}

// jsonNumber renders a JSON number the way python's str() would: an integer
// without a decimal point.
func jsonNumber(value float64) string {
	if value == math.Trunc(value) && math.Abs(value) < 1e15 {
		return strconv.FormatInt(int64(value), 10)
	}
	return strconv.FormatFloat(value, 'g', -1, 64)
}

// jsonRepr renders a JSON value the way python's repr() would, for the
// messages that quote a value that is not what it should be.
func jsonRepr(value any) string {
	switch v := value.(type) {
	case nil:
		return "None"
	case bool:
		if v {
			return "True"
		}
		return "False"
	case string:
		return pycompat.Repr(v)
	case float64:
		return jsonNumber(v)
	default:
		return jsonString(value)
	}
}

// valueOr reads a field and renders it the way python's f-string would.
//
// The run id of a measurement is a NUMBER in the file and a string in the
// message, so the value cannot be read as a string: python renders whatever
// it finds.
func valueOr(m map[string]any, key, fallback string) string {
	value, ok := m[key]
	if !ok {
		return fallback
	}
	return jsonString(value)
}

// median is the median of a list of numbers.
func median(values []float64) float64 {
	if len(values) == 0 {
		return 0
	}
	ordered := make([]float64, len(values))
	copy(ordered, values)
	sort.Float64s(ordered)
	middle := len(ordered) / 2
	if len(ordered)%2 == 1 {
		return ordered[middle]
	}
	return (ordered[middle-1] + ordered[middle]) / 2.0
}

// parseFloat parses a float the way python's float() would.
func parseFloat(text string) (float64, bool) {
	value, err := strconv.ParseFloat(text, 64)
	if err != nil {
		return 0, false
	}
	return value, true
}

// parseInt parses an int the way python's int() would.
func parseInt(text string) (int, bool) {
	value, err := strconv.Atoi(text)
	if err != nil {
		return 0, false
	}
	return value, true
}

// inf is python's float("inf").
func inf() float64 { return math.Inf(1) }
