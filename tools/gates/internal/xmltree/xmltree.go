// Package xmltree is a small XML reader that answers the questions the
// gates ask of a document.
//
// The python originals used xml.etree.ElementTree, and the gates depend on
// two of its behaviours that encoding/xml does not share out of the box:
//
//   - ElementTree's `find`/`findall` match a tag EXACTLY, so a document
//     that declares a default namespace has none of the tags a gate looks
//     for. `FindChild` and `Iter` keep that behaviour.
//   - ElementTree's `el.text` is the text BETWEEN the start tag and the
//     first child, not the concatenation of every text node. `Text` keeps
//     that behaviour, and the difference matters: a `<description>` that
//     holds a nested element would otherwise read differently here.
//
// `ChildAny` is the namespace-AGNOSTIC lookup the python originals used
// where they wrote "the first child whose tag ends with `}` + name".
package xmltree

import (
	"bytes"
	"encoding/xml"
	"errors"
	"io"
	"strings"
)

// Node is one element of an XML document.
type Node struct {
	Name     xml.Name
	Attrs    map[string]string
	Children []*Node
	// Text is the text between the start tag and the first child, which is
	// what python's ElementTree calls `el.text`.
	Text string
}

// Parse reads a document and returns its root element.
func Parse(data []byte) (*Node, error) {
	dec := xml.NewDecoder(bytes.NewReader(data))
	var stack []*Node
	var root *Node
	for {
		tok, err := dec.Token()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return nil, err
		}
		switch t := tok.(type) {
		case xml.StartElement:
			node := &Node{Name: t.Name, Attrs: map[string]string{}}
			for _, attr := range t.Attr {
				node.Attrs[attr.Name.Local] = attr.Value
			}
			if len(stack) > 0 {
				parent := stack[len(stack)-1]
				parent.Children = append(parent.Children, node)
			} else if root == nil {
				root = node
			}
			stack = append(stack, node)
		case xml.EndElement:
			if len(stack) > 0 {
				stack = stack[:len(stack)-1]
			}
		case xml.CharData:
			if len(stack) > 0 {
				current := stack[len(stack)-1]
				if len(current.Children) == 0 {
					current.Text += string(t)
				}
			}
		}
	}
	if root == nil {
		return nil, errors.New("the document has no root element")
	}
	return root, nil
}

// Attr returns an attribute's value, or the empty string.
func (n *Node) Attr(name string) string {
	if n == nil {
		return ""
	}
	return n.Attrs[name]
}

// TrimmedText returns the element's text with the surrounding whitespace
// removed, which is what the python originals' `_text` did.
func (n *Node) TrimmedText() string {
	if n == nil {
		return ""
	}
	return strings.TrimSpace(n.Text)
}

// ChildAny returns the first child whose local name is `name`, whatever
// namespace it is in.
//
// This is the python originals' `_child`: `c.tag == name or
// c.tag.endswith("}" + name)`.
func (n *Node) ChildAny(name string) *Node {
	if n == nil {
		return nil
	}
	for _, child := range n.Children {
		if child.Name.Local == name {
			return child
		}
	}
	return nil
}

// FindChild returns the first child whose tag is exactly `name`, in NO
// namespace.
//
// This is python's `element.find(name)`, which does not match a namespaced
// tag.
func (n *Node) FindChild(name string) *Node {
	if n == nil {
		return nil
	}
	for _, child := range n.Children {
		if child.Name.Local == name && child.Name.Space == "" {
			return child
		}
	}
	return nil
}

// FindChildren returns every direct child whose tag is exactly `name`, in
// NO namespace. This is python's `element.findall(name)`.
func (n *Node) FindChildren(name string) []*Node {
	if n == nil {
		return nil
	}
	var out []*Node
	for _, child := range n.Children {
		if child.Name.Local == name && child.Name.Space == "" {
			out = append(out, child)
		}
	}
	return out
}

// Iter returns every element of the subtree whose tag is exactly `name`, in
// NO namespace, in document order. This is python's `element.iter(name)`.
func (n *Node) Iter(name string) []*Node {
	var out []*Node
	var walk func(node *Node)
	walk = func(node *Node) {
		if node.Name.Local == name && node.Name.Space == "" {
			out = append(out, node)
		}
		for _, child := range node.Children {
			walk(child)
		}
	}
	walk(n)
	return out
}

// IterNS returns every element of the subtree whose tag is `name` in the
// namespace `space`, in document order.
func (n *Node) IterNS(space, name string) []*Node {
	var out []*Node
	var walk func(node *Node)
	walk = func(node *Node) {
		if node.Name.Local == name && node.Name.Space == space {
			out = append(out, node)
		}
		for _, child := range node.Children {
			walk(child)
		}
	}
	walk(n)
	return out
}

// FindChildNS returns the first child whose tag is `name` in the namespace
// `space`.
func (n *Node) FindChildNS(space, name string) *Node {
	if n == nil {
		return nil
	}
	for _, child := range n.Children {
		if child.Name.Local == name && child.Name.Space == space {
			return child
		}
	}
	return nil
}
