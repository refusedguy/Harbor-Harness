// flow.go -- the reader for FLOW collections: the `{a: 1}` and `[a, b]` that
// can be written where a value belongs.
//
// A flow mapping is a mapping, and YAML forbids a duplicate key in it exactly
// as it forbids one in a block mapping:
//
//	with: {a: 1, a: 2}     <- a duplicate, and a file GitHub will not read
//
// The block scanner of the package does not see this shape: the whole
// collection is on ONE line, so the block structure offers nothing to walk.
// The reader below walks the collection itself.
//
// The reader is deliberately narrow. It reads the collections that are
// written on one line, and it reports the mappings among them that declare
// the same key twice. A collection that spans several lines is left to the
// block scanner, which sees those lines as the block mapping they read as —
// and which finds the same duplicates there.

package checkworkflows

import "strings"

// flowReader walks one flow collection.
type flowReader struct {
	text     string
	pos      int
	depth    int
	path     string
	line     int
	findings []finding
}

// flowFindings reports the duplicate keys of a flow collection written on one
// line.
//
// An empty result means one of two things: there is no flow collection, or
// there is one and it declares every key once. A collection that is not
// closed on this line belongs to the block scanner.
func flowFindings(text string, path string, line int) []finding {
	trimmed := strings.TrimSpace(text)
	if trimmed == "" {
		return nil
	}
	if trimmed[0] != '{' && trimmed[0] != '[' {
		return nil
	}
	reader := &flowReader{text: trimmed, path: path, line: line}
	reader.readValue()
	if reader.depth != 0 {
		// The collection spans several lines. The block scanner reads those
		// lines; this reader does not guess about them.
		return nil
	}
	return reader.findings
}

// skipSpace moves over the blanks of the collection.
func (r *flowReader) skipSpace() {
	for r.pos < len(r.text) && (r.text[r.pos] == ' ' || r.text[r.pos] == '\t') {
		r.pos++
	}
}

// readValue reads one value of a collection.
func (r *flowReader) readValue() {
	r.skipSpace()
	if r.pos >= len(r.text) {
		return
	}
	switch r.text[r.pos] {
	case '{':
		r.readMapping()
	case '[':
		r.readSequence()
	default:
		r.readScalar(false)
	}
}

// readMapping reads `{key: value, ...}` and reports the keys it declares
// twice.
func (r *flowReader) readMapping() {
	r.pos++ // the '{'
	r.depth++
	seen := map[string]bool{}
	for {
		r.skipSpace()
		if r.pos >= len(r.text) {
			return
		}
		if r.text[r.pos] == '}' {
			r.pos++
			r.depth--
			return
		}
		key, ok := r.readKey()
		if !ok {
			r.skipToDelimiter()
			continue
		}
		r.skipSpace()
		if r.pos >= len(r.text) {
			return
		}
		if r.text[r.pos] != ':' {
			// Not an entry of the mapping. Skip to the next delimiter and
			// keep reading: a collection GitHub will not read is not the
			// gate's business beyond the duplicates it holds.
			r.skipToDelimiter()
			continue
		}
		r.pos++ // the ':'
		r.readValue()
		if seen[key] {
			r.findings = append(r.findings, finding{
				path: r.path,
				line: r.line,
				kind: kindDuplicate,
				key:  key,
				note: "in a flow mapping",
			})
		} else {
			seen[key] = true
		}
		r.skipSpace()
		if r.pos >= len(r.text) {
			return
		}
		if r.text[r.pos] == ',' {
			r.pos++
			continue
		}
		if r.text[r.pos] == '}' {
			r.pos++
			r.depth--
			return
		}
		r.skipToDelimiter()
	}
}

// readSequence reads `[a, b]` and the mappings inside it.
func (r *flowReader) readSequence() {
	r.pos++ // the '['
	r.depth++
	for {
		r.skipSpace()
		if r.pos >= len(r.text) {
			return
		}
		if r.text[r.pos] == ']' {
			r.pos++
			r.depth--
			return
		}
		r.readValue()
		r.skipSpace()
		if r.pos >= len(r.text) {
			return
		}
		if r.text[r.pos] == ',' {
			r.pos++
			continue
		}
		if r.text[r.pos] == ']' {
			r.pos++
			r.depth--
			return
		}
		r.skipToDelimiter()
	}
}

// readKey reads the key of an entry of a flow mapping.
func (r *flowReader) readKey() (string, bool) {
	if r.pos < len(r.text) && (r.text[r.pos] == '"' || r.text[r.pos] == '\'') {
		return r.readScalar(true)
	}
	start := r.pos
	for r.pos < len(r.text) && r.text[r.pos] != ':' {
		r.pos++
	}
	if r.pos >= len(r.text) {
		return "", false
	}
	return strings.TrimSpace(r.text[start:r.pos]), true
}

// readScalar reads a plain or a quoted scalar.
//
// In the key position a plain scalar ends at the colon; in the value position
// it ends at the next delimiter, and a colon inside it is part of the value.
func (r *flowReader) readScalar(isKey bool) (string, bool) {
	if r.pos < len(r.text) && (r.text[r.pos] == '"' || r.text[r.pos] == '\'') {
		quote := r.text[r.pos]
		r.pos++
		start := r.pos
		for r.pos < len(r.text) && r.text[r.pos] != quote {
			r.pos++
		}
		content := r.text[start:r.pos]
		if r.pos < len(r.text) {
			r.pos++ // the closing quote
		}
		return content, true
	}
	start := r.pos
	for r.pos < len(r.text) {
		c := r.text[r.pos]
		if c == ',' || c == '}' || c == ']' {
			break
		}
		if isKey && c == ':' {
			break
		}
		r.pos++
	}
	return strings.TrimSpace(r.text[start:r.pos]), true
}

// skipToDelimiter moves over whatever is left until the next delimiter of the
// collection.
func (r *flowReader) skipToDelimiter() {
	for r.pos < len(r.text) {
		c := r.text[r.pos]
		if c == ',' || c == '}' || c == ']' {
			return
		}
		r.pos++
	}
}
