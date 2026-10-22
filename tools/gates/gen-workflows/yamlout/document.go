// Package yamlout writes YAML text.
//
// # WHY A WRITER AND NOT A PARSER
//
// The standard library of Go has no YAML, and this repository takes no
// third-party dependency, so the generator cannot read YAML. It does not
// have to: the SOURCE OF TRUTH is Go code, and the YAML is what the writer
// produces. The only YAML this repository has to READ is the workflow
// files, and the gate that reads them (check-workflows) reads their SHAPE
// with a scanner of its own.
//
// # WHY THE WRITER IS AN EXPLICIT TREE AND NOT A REFLECTION
//
// The tree is explicit so that the output is a function of the tree and of
// nothing else. Two runs of the generator on the same tree produce the same
// bytes, always: there is no map anywhere in the model, every mapping keeps
// its pairs in the order the caller wrote them, and every scalar says how it
// is spelled. A generator whose output depends on map iteration order
// cannot be checked by comparing bytes, and a check that cannot be made
// byte-exact is a check nobody trusts.
//
// # WHAT THE TREE CAN SAY
//
// What the 17 workflow files of this repository actually contain, and no
// more: block mappings, block sequences, flow sequences, four scalar
// spellings, comments, and blank lines. There are no YAML anchors and no
// aliases in them (measured: 0), no flow mappings (the one that a grep
// finds is shell text inside a `run:` body), and no quoted keys. The tree
// therefore does not model anchors, and that is a fact about the files, not
// an oversight.
package yamlout

import (
	"strings"
)

// Kind is what a node is.
type Kind int

const (
	// Scalar is a single value.
	Scalar Kind = iota
	// Mapping is an ordered set of key/value pairs.
	Mapping
	// Sequence is an ordered list of values.
	Sequence
)

// Style is how a scalar is spelled.
//
// The style is part of the model, not a decision the writer makes: the
// workflow files of this repository spell some strings plainly and others
// in double quotes, and the generated file has to be byte-identical to the
// committed one. A writer that "normalised" the quoting would produce a
// different file on the first run, and the comparison the gate makes would
// then be a comparison of two spellings rather than of two trees.
type Style int

const (
	// Plain writes the text as it is, unquoted.
	Plain Style = iota
	// Double writes the text in double quotes.
	Double
	// Single writes the text in single quotes.
	Single
	// Literal writes the text as a block scalar: a `|` and the lines.
	Literal
)

// Node is one value of a YAML document.
type Node struct {
	// Kind says what the node is.
	Kind Kind
	// Style says how a scalar is spelled.
	Style Style
	// Text is the text of a scalar: the value itself, unescaped, or the
	// body of a block scalar, with "\n" between the lines.
	Text string
	// Pairs are the pairs of a mapping, in the order the caller wrote them.
	Pairs []Pair
	// Items are the items of a sequence, in the order the caller wrote them.
	Items []Item
	// Flow says that a sequence is written on one line: `[a, b]`.
	Flow bool
}

// Pair is one key/value pair of a mapping.
type Pair struct {
	// Key is the name of the pair.
	Key string
	// Value is what the key holds.
	Value *Node
	// Before are comment lines printed above the key.
	Before []string
	// Blank says that a blank line precedes the key.
	Blank bool
}

// Item is one entry of a sequence.
type Item struct {
	// Value is what the entry holds.
	Value *Node
	// Before are comment lines printed above the `-` marker.
	Before []string
	// Blank says that a blank line precedes the entry.
	Blank bool
}

// Doc is a YAML document: a header of comments and a root node.
type Doc struct {
	// Header are the comment lines at the top of the file. Each one is
	// written as `#` followed by the text.
	Header []string
	// Root is the top-level node, a mapping.
	Root *Node
}

// Str is a plain scalar: the text as it is, unquoted.
//
// The caller says "plain" for a text that YAML reads as it stands: a label,
// an action reference, a branch name.
func Str(text string) *Node {
	return &Node{Kind: Scalar, Style: Plain, Text: text}
}

// Quoted is a scalar in double quotes.
func Quoted(text string) *Node {
	return &Node{Kind: Scalar, Style: Double, Text: text}
}

// Bool is a boolean scalar.
func Bool(value bool) *Node {
	if value {
		return Str("true")
	}
	return Str("false")
}

// Int is an integer scalar.
func Int(value int) *Node {
	return Str(itoa(value))
}

// Block is a block scalar: `|` and the body, one line per line of the
// text.
//
// The body is written at the indent of the key plus two, which is what a
// YAML reader expects of a literal block.
func Block(body string) *Node {
	return &Node{Kind: Scalar, Style: Literal, Text: body}
}

// Map is a mapping of the given pairs.
func Map(pairs ...Pair) *Node {
	return &Node{Kind: Mapping, Pairs: pairs}
}

// Seq is a block sequence of the given items.
func Seq(items ...Item) *Node {
	return &Node{Kind: Sequence, Items: items}
}

// FlowSeq is a sequence written on one line: `[a, b]`.
func FlowSeq(items ...*Node) *Node {
	return &Node{Kind: Sequence, Flow: true, Items: itemsOf(items)}
}

// P is a pair: a key and a value.
func P(key string, value *Node) Pair {
	return Pair{Key: key, Value: value}
}

// PB is a pair with a blank line above it.
func PB(key string, value *Node) Pair {
	return Pair{Key: key, Value: value, Blank: true}
}

// PC is a pair with comment lines above it.
func PC(key string, value *Node, comments ...string) Pair {
	return Pair{Key: key, Value: value, Before: comments}
}

// I is a sequence item: a value.
func I(value *Node) Item {
	return Item{Value: value}
}

// IB is a sequence item with a blank line above it.
func IB(value *Node) Item {
	return Item{Value: value, Blank: true}
}

// IC is a sequence item with comment lines above it.
func IC(value *Node, comments ...string) Item {
	return Item{Value: value, Before: comments}
}

func itemsOf(nodes []*Node) []Item {
	items := make([]Item, 0, len(nodes))
	for _, node := range nodes {
		items = append(items, Item{Value: node})
	}
	return items
}

// itoa is strconv.Itoa without the import: the writer needs one number
// format and one only.
func itoa(value int) string {
	if value == 0 {
		return "0"
	}
	negative := value < 0
	if negative {
		value = -value
	}
	var digits [20]byte
	position := len(digits)
	for value > 0 {
		position--
		digits[position] = byte('0' + value%10)
		value /= 10
	}
	if negative {
		position--
		digits[position] = '-'
	}
	return string(digits[position:])
}

// Render writes the document as YAML text.
//
// The result always ends with a newline, and it is a function of the tree
// alone: the same tree renders to the same bytes on every machine and in
// every order of evaluation.
func (d *Doc) Render() string {
	var builder strings.Builder
	for _, line := range d.Header {
		writeComment(&builder, line, 0)
	}
	writeMapping(&builder, d.Root, 0)
	return builder.String()
}

func writeComment(builder *strings.Builder, text string, indent int) {
	builder.WriteString(strings.Repeat(" ", indent))
	builder.WriteString("#")
	if text != "" {
		builder.WriteString(" ")
		builder.WriteString(text)
	}
	builder.WriteString("\n")
}

// writeMapping writes the pairs of a mapping, each at the given indent.
func writeMapping(builder *strings.Builder, node *Node, indent int) {
	for _, pair := range node.Pairs {
		writePair(builder, pair, indent)
	}
}

// writePair writes one pair: its comments, its key, and its value.
func writePair(builder *strings.Builder, pair Pair, indent int) {
	if pair.Blank {
		builder.WriteString("\n")
	}
	for _, comment := range pair.Before {
		writeComment(builder, comment, indent)
	}
	builder.WriteString(strings.Repeat(" ", indent))
	builder.WriteString(pair.Key)
	builder.WriteString(":")
	writeValue(builder, pair.Value, indent)
}

// writeValue writes the value of a pair: the value itself, or the block it
// opens.
func writeValue(builder *strings.Builder, node *Node, indent int) {
	switch node.Kind {
	case Scalar:
		writeScalar(builder, node, indent)
	case Mapping:
		builder.WriteString("\n")
		writeMapping(builder, node, indent+2)
	case Sequence:
		if node.Flow {
			builder.WriteString(" ")
			builder.WriteString(flowSequence(node))
			builder.WriteString("\n")
			return
		}
		builder.WriteString("\n")
		writeSequence(builder, node, indent+2)
	}
}

// writeScalar writes one scalar in the spelling the model asks for.
func writeScalar(builder *strings.Builder, node *Node, indent int) {
	switch node.Style {
	case Literal:
		builder.WriteString(" |\n")
		body := strings.TrimRight(node.Text, "\n")
		for _, line := range strings.Split(body, "\n") {
			if line == "" {
				builder.WriteString("\n")
				continue
			}
			builder.WriteString(strings.Repeat(" ", indent+2))
			builder.WriteString(line)
			builder.WriteString("\n")
		}
	case Double:
		builder.WriteString(" \"")
		builder.WriteString(escapeDouble(node.Text))
		builder.WriteString("\"\n")
	case Single:
		builder.WriteString(" '")
		builder.WriteString(strings.ReplaceAll(node.Text, "'", "''"))
		builder.WriteString("'\n")
	default:
		builder.WriteString(" ")
		builder.WriteString(node.Text)
		builder.WriteString("\n")
	}
}

// writeSequence writes a block sequence: the entries, each with its `-`
// marker at the given indent.
func writeSequence(builder *strings.Builder, node *Node, indent int) {
	for _, item := range node.Items {
		if item.Blank {
			builder.WriteString("\n")
		}
		for _, comment := range item.Before {
			writeComment(builder, comment, indent)
		}
		builder.WriteString(strings.Repeat(" ", indent))
		builder.WriteString("-")
		switch item.Value.Kind {
		case Mapping:
			writeMappingInline(builder, item.Value, indent+2)
		case Scalar:
			builder.WriteString(" ")
			writeScalarInline(builder, item.Value, indent+2)
		default:
			builder.WriteString("\n")
			writeValue(builder, item.Value, indent)
		}
	}
}

// writeMappingInline writes a mapping whose first pair shares the line of
// the `-` marker that opens it.
//
// That is the shape of a step: `- name: Build` and then the rest of the
// step's keys, two columns further right.
func writeMappingInline(builder *strings.Builder, node *Node, indent int) {
	for index, pair := range node.Pairs {
		if index == 0 {
			builder.WriteString(" ")
			builder.WriteString(pair.Key)
			builder.WriteString(":")
			writeValue(builder, pair.Value, indent)
			continue
		}
		writePair(builder, pair, indent)
	}
}

// writeScalarInline writes a scalar that shares the line of the `-` marker
// that opens it.
func writeScalarInline(builder *strings.Builder, node *Node, indent int) {
	switch node.Style {
	case Double:
		builder.WriteString("\"")
		builder.WriteString(escapeDouble(node.Text))
		builder.WriteString("\"")
	case Single:
		builder.WriteString("'")
		builder.WriteString(strings.ReplaceAll(node.Text, "'", "''"))
		builder.WriteString("'")
	default:
		builder.WriteString(node.Text)
	}
}

// flowSequence writes a sequence on one line: `[a, b]`.
func flowSequence(node *Node) string {
	parts := make([]string, 0, len(node.Items))
	for _, item := range node.Items {
		var builder strings.Builder
		writeScalarInline(&builder, item.Value, 0)
		parts = append(parts, builder.String())
	}
	return "[" + strings.Join(parts, ", ") + "]"
}

// escapeDouble escapes the characters a double-quoted YAML scalar may not
// hold unescaped.
func escapeDouble(text string) string {
	var builder strings.Builder
	for _, r := range text {
		switch r {
		case '"':
			builder.WriteString("\\\"")
		case '\\':
			builder.WriteString("\\\\")
		case '\n':
			builder.WriteString("\\n")
		case '\t':
			builder.WriteString("\\t")
		default:
			builder.WriteRune(r)
		}
	}
	return builder.String()
}
