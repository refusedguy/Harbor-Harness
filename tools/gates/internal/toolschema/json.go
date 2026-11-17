package toolschema

// json.go -- the ordered JSON value the schema is written in.
//
// WHY A HAND-WRITTEN VALUE AND NOT encoding/json
//
//   The canonical form of a schema is its compact JSON text with the keys in
//   DECLARATION order, and the pin is the SHA-256 of that text. A Go map
//   does not remember an order, and encoding/json sorts map keys, so a
//   schema hashed through it would not hash to the value the python model
//   wrote into the pin. The value below keeps the order, and its writer
//   escapes strings the way json.dumps(..., ensure_ascii=False) escapes
//   them -- which is the escaping the pin was written with.
//
//   The same text is what the compiler renders: the model is the ONE reading
//   of a tool's declaration, and the pin is what the gate and the compiler
//   share.

import (
	"fmt"
	"strconv"
	"strings"
)

// Kind is the JSON kind of a value.
type Kind int

// The JSON kinds.
const (
	KindString Kind = iota
	KindNumber
	KindBool
	KindNull
	KindArray
	KindObject
)

// Value is a JSON value that remembers the order of an object's keys.
//
// The order IS the point: the canonical text of a schema is the compact JSON
// with the keys in declaration order, and the digest is taken over that
// text. A map would lose it.
type Value struct {
	Kind Kind
	Str  string
	// Num is the number's TEXT, already canonical: an integer keeps its
	// decimal form, a float its shortest round-trip form.
	Num  string
	Bool bool
	Arr  []*Value
	// Keys and Vals are parallel: Vals[i] is the value of Keys[i].
	Keys []string
	Vals []*Value
}

// NewString is a JSON string.
func NewString(s string) *Value { return &Value{Kind: KindString, Str: s} }

// NewNumber is a JSON number with the given canonical text.
func NewNumber(text string) *Value { return &Value{Kind: KindNumber, Num: text} }

// NewBool is a JSON boolean.
func NewBool(b bool) *Value { return &Value{Kind: KindBool, Bool: b} }

// NewObject is an empty JSON object.
func NewObject() *Value { return &Value{Kind: KindObject} }

// NewArray is an empty JSON array.
func NewArray() *Value { return &Value{Kind: KindArray} }

// Set puts a key into an object.
//
// A key that is already there keeps its PLACE and takes the new value: that
// is what a python dict does, and the order is what the digest is taken
// over.
func (v *Value) Set(key string, value *Value) {
	for i, k := range v.Keys {
		if k == key {
			v.Vals[i] = value
			return
		}
	}
	v.Keys = append(v.Keys, key)
	v.Vals = append(v.Vals, value)
}

// Get returns the value of a key, or nil when the object does not hold it.
func (v *Value) Get(key string) *Value {
	for i, k := range v.Keys {
		if k == key {
			return v.Vals[i]
		}
	}
	return nil
}

// Append adds an element to an array.
func (v *Value) Append(value *Value) { v.Arr = append(v.Arr, value) }

// Canonical is the compact JSON text of the value: no whitespace, the keys
// in the order they were set, and the strings escaped the way python's
// json.dumps(ensure_ascii=False) escapes them.
func (v *Value) Canonical() string {
	var sb strings.Builder
	v.write(&sb)
	return sb.String()
}

func (v *Value) write(sb *strings.Builder) {
	switch v.Kind {
	case KindString:
		writeJSONString(sb, v.Str)
	case KindNumber:
		sb.WriteString(v.Num)
	case KindBool:
		if v.Bool {
			sb.WriteString("true")
		} else {
			sb.WriteString("false")
		}
	case KindNull:
		sb.WriteString("null")
	case KindArray:
		sb.WriteByte('[')
		for i, element := range v.Arr {
			if i > 0 {
				sb.WriteByte(',')
			}
			element.write(sb)
		}
		sb.WriteByte(']')
	case KindObject:
		sb.WriteByte('{')
		for i, key := range v.Keys {
			if i > 0 {
				sb.WriteByte(',')
			}
			writeJSONString(sb, key)
			sb.WriteByte(':')
			v.Vals[i].write(sb)
		}
		sb.WriteByte('}')
	}
}

// writeJSONString writes a JSON string literal.
//
// The escaping is python's, not Go's: Go's encoding/json escapes <, > and &
// and writes every non-ASCII character as \uXXXX, and the pin was written
// with neither. A non-ASCII character is written as itself here, which is
// what ensure_ascii=False means.
func writeJSONString(sb *strings.Builder, s string) {
	sb.WriteByte('"')
	for _, r := range s {
		switch r {
		case '"':
			sb.WriteString(`\"`)
		case '\\':
			sb.WriteString(`\\`)
		case '\n':
			sb.WriteString(`\n`)
		case '\r':
			sb.WriteString(`\r`)
		case '\t':
			sb.WriteString(`\t`)
		case '\b':
			sb.WriteString(`\b`)
		case '\f':
			sb.WriteString(`\f`)
		default:
			if r < 0x20 {
				sb.WriteString(fmt.Sprintf(`\u%04x`, r))
			} else {
				sb.WriteRune(r)
			}
		}
	}
	sb.WriteByte('"')
}

// ParseJSON reads a JSON text into an ordered value.
//
// The parser exists for the LEGACY form: a tool that still carries its
// schema as a raw JSON string literal is read back through it, and the
// result is re-serialised in the canonical form. The order of the keys is
// kept, because the literal's order is the order the schema was declared
// in.
func ParseJSON(text string) (*Value, error) {
	p := &jsonParser{text: text}
	p.skipSpace()
	value, err := p.value()
	if err != nil {
		return nil, err
	}
	p.skipSpace()
	if p.pos != len(p.text) {
		return nil, fmt.Errorf("trailing text at offset %d", p.pos)
	}
	return value, nil
}

type jsonParser struct {
	text string
	pos  int
}

func (p *jsonParser) skipSpace() {
	for p.pos < len(p.text) {
		switch p.text[p.pos] {
		case ' ', '\t', '\n', '\r':
			p.pos++
		default:
			return
		}
	}
}

func (p *jsonParser) value() (*Value, error) {
	if p.pos >= len(p.text) {
		return nil, fmt.Errorf("unexpected end of JSON")
	}
	switch c := p.text[p.pos]; {
	case c == '{':
		return p.object()
	case c == '[':
		return p.array()
	case c == '"':
		s, err := p.string()
		if err != nil {
			return nil, err
		}
		return NewString(s), nil
	case c == 't':
		if strings.HasPrefix(p.text[p.pos:], "true") {
			p.pos += 4
			return NewBool(true), nil
		}
	case c == 'f':
		if strings.HasPrefix(p.text[p.pos:], "false") {
			p.pos += 5
			return NewBool(false), nil
		}
	case c == 'n':
		if strings.HasPrefix(p.text[p.pos:], "null") {
			p.pos += 4
			return &Value{Kind: KindNull}, nil
		}
	case c == '-' || (c >= '0' && c <= '9'):
		return p.number()
	}
	return nil, fmt.Errorf("unexpected character %q at offset %d", p.text[p.pos], p.pos)
}

func (p *jsonParser) object() (*Value, error) {
	p.pos++ // '{'
	object := NewObject()
	p.skipSpace()
	if p.pos < len(p.text) && p.text[p.pos] == '}' {
		p.pos++
		return object, nil
	}
	for {
		p.skipSpace()
		if p.pos >= len(p.text) || p.text[p.pos] != '"' {
			return nil, fmt.Errorf("expected a key at offset %d", p.pos)
		}
		key, err := p.string()
		if err != nil {
			return nil, err
		}
		p.skipSpace()
		if p.pos >= len(p.text) || p.text[p.pos] != ':' {
			return nil, fmt.Errorf("expected ':' at offset %d", p.pos)
		}
		p.pos++
		p.skipSpace()
		item, err := p.value()
		if err != nil {
			return nil, err
		}
		object.Set(key, item)
		p.skipSpace()
		if p.pos >= len(p.text) {
			return nil, fmt.Errorf("unexpected end of JSON object")
		}
		switch p.text[p.pos] {
		case ',':
			p.pos++
		case '}':
			p.pos++
			return object, nil
		default:
			return nil, fmt.Errorf("expected ',' or '}' at offset %d", p.pos)
		}
	}
}

func (p *jsonParser) array() (*Value, error) {
	p.pos++ // '['
	array := NewArray()
	p.skipSpace()
	if p.pos < len(p.text) && p.text[p.pos] == ']' {
		p.pos++
		return array, nil
	}
	for {
		p.skipSpace()
		item, err := p.value()
		if err != nil {
			return nil, err
		}
		array.Append(item)
		p.skipSpace()
		if p.pos >= len(p.text) {
			return nil, fmt.Errorf("unexpected end of JSON array")
		}
		switch p.text[p.pos] {
		case ',':
			p.pos++
		case ']':
			p.pos++
			return array, nil
		default:
			return nil, fmt.Errorf("expected ',' or ']' at offset %d", p.pos)
		}
	}
}

func (p *jsonParser) string() (string, error) {
	p.pos++ // the opening quote
	var sb strings.Builder
	for p.pos < len(p.text) {
		c := p.text[p.pos]
		switch c {
		case '"':
			p.pos++
			return sb.String(), nil
		case '\\':
			p.pos++
			if p.pos >= len(p.text) {
				return "", fmt.Errorf("unexpected end of JSON string")
			}
			escape := p.text[p.pos]
			p.pos++
			switch escape {
			case '"':
				sb.WriteByte('"')
			case '\\':
				sb.WriteByte('\\')
			case '/':
				sb.WriteByte('/')
			case 'b':
				sb.WriteByte('\b')
			case 'f':
				sb.WriteByte('\f')
			case 'n':
				sb.WriteByte('\n')
			case 'r':
				sb.WriteByte('\r')
			case 't':
				sb.WriteByte('\t')
			case 'u':
				if p.pos+4 > len(p.text) {
					return "", fmt.Errorf("truncated \\u escape")
				}
				code, err := strconv.ParseUint(p.text[p.pos:p.pos+4], 16, 32)
				if err != nil {
					return "", fmt.Errorf("bad \\u escape at offset %d", p.pos)
				}
				p.pos += 4
				sb.WriteRune(rune(code))
			default:
				return "", fmt.Errorf("unknown escape %q", escape)
			}
		default:
			sb.WriteByte(c)
			p.pos++
		}
	}
	return "", fmt.Errorf("unterminated JSON string")
}

func (p *jsonParser) number() (*Value, error) {
	start := p.pos
	if p.pos < len(p.text) && p.text[p.pos] == '-' {
		p.pos++
	}
	for p.pos < len(p.text) && p.text[p.pos] >= '0' && p.text[p.pos] <= '9' {
		p.pos++
	}
	isFloat := false
	if p.pos < len(p.text) && p.text[p.pos] == '.' {
		isFloat = true
		p.pos++
		for p.pos < len(p.text) && p.text[p.pos] >= '0' && p.text[p.pos] <= '9' {
			p.pos++
		}
	}
	if p.pos < len(p.text) && (p.text[p.pos] == 'e' || p.text[p.pos] == 'E') {
		isFloat = true
		p.pos++
		if p.pos < len(p.text) && (p.text[p.pos] == '+' || p.text[p.pos] == '-') {
			p.pos++
		}
		for p.pos < len(p.text) && p.text[p.pos] >= '0' && p.text[p.pos] <= '9' {
			p.pos++
		}
	}
	text := p.text[start:p.pos]
	if text == "" || text == "-" {
		return nil, fmt.Errorf("bad number at offset %d", start)
	}
	if !isFloat {
		// An integer keeps its decimal text. python's json.loads gives an
		// int and json.dumps writes it back the same way; the one
		// difference is "-0", which python normalises to "0".
		if text == "-0" {
			text = "0"
		}
		return NewNumber(text), nil
	}
	f, err := strconv.ParseFloat(text, 64)
	if err != nil {
		return nil, fmt.Errorf("bad number %q: %w", text, err)
	}
	return NewNumber(pythonFloat(f)), nil
}

// pythonFloat formats a float the way python's repr does.
//
// The shortest round-trip form, with a decimal point on a whole number: repr
// writes 1.0 where Go's %g writes 1.
func pythonFloat(f float64) string {
	text := strconv.FormatFloat(f, 'g', -1, 64)
	if !strings.ContainsAny(text, ".eE") {
		text += ".0"
	}
	return text
}
