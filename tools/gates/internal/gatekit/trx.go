package gatekit

import (
	"bytes"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// TRXNS is the namespace of the TRX schema.
//
// The python originals wrote it as "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
// and matched on the brace form. Here it is the URI, and the match is on
// the URI AND the local name: a TRX without the namespace is a file the
// python original read as empty, and this must read it as empty too.
const TRXNS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

// OutputChunk is one child of an <Output> element that carries text.
type OutputChunk struct {
	// Tag is the local name: "StdOut", "StdErr" or "DebugTrace".
	Tag string
	// Text is the character data of the element.
	Text string
}

// TRXResult is one <UnitTestResult> of a TRX file.
type TRXResult struct {
	TestID   string
	TestName string
	Outcome  string
	Duration string
	// Output holds the text-bearing children of the <Output> element, in
	// document order. The order matters: the python originals joined the
	// chunks of StdOut and StdErr in the order the file lists them.
	Output []OutputChunk
}

// DebugTrace returns the first non-empty <DebugTrace> of the result.
func (r TRXResult) DebugTrace() string {
	for _, chunk := range r.Output {
		if chunk.Tag == "DebugTrace" && chunk.Text != "" {
			return chunk.Text
		}
	}
	return ""
}

// TRXTimes is one <Times> window of a TRX file.
type TRXTimes struct {
	Creation string
	Finish   string
}

// TRX is a parsed TRX file.
type TRX struct {
	// Owners maps a test id to "Class.Method". The definition carries the
	// class; the result carries the name.
	Owners map[string]string
	// Results are the <UnitTestResult> elements, in document order.
	Results []TRXResult
	// Times are the <Times> elements, in document order.
	Times []TRXTimes
}

// ParseTRX reads a TRX file.
//
// A file that is not a TRX, or that is a TRX with broken XML, is an error:
// the python originals printed a warning and moved on to the next file.
func ParseTRX(path string) (*TRX, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return ParseTRXBytes(data)
}

// ParseTRXBytes reads a TRX from a byte slice.
func ParseTRXBytes(data []byte) (*TRX, error) {
	decoder := xml.NewDecoder(bytes.NewReader(data))
	out := &TRX{Owners: map[string]string{}}
	// The element stack. Each frame remembers whether its character data
	// has been seen already: the python originals read `node.text`, which
	// is the text BEFORE the first child element, not all of it.
	type frame struct {
		space   string
		local   string
		attrs   map[string]string
		text    string
		hasText bool
	}
	stack := []frame{}

	// The index of the <UnitTestResult> that is open, or -1.
	current := -1
	// Whether an <Output> of that result is open.
	inOutput := false
	// The unit context: the <UnitTest> the current TestMethod is in.
	unitID, unitName := "", ""
	// The start offset of the last element that was opened: an unclosed
	// token is reported at the place it started.
	lastStart := int64(-1)

	for {
		tokenStart := decoder.InputOffset()
		token, err := decoder.Token()
		if err == io.EOF {
			if lastStart < 0 {
				return nil, fmt.Errorf("no element found: %s", lineColumn(data, decoder.InputOffset()))
			}
			break
		}
		if err != nil {
			return nil, fmt.Errorf("%s", xmlPosition(data, decoder.InputOffset(), err, lastStart))
		}
		switch t := token.(type) {
		case xml.StartElement:
			lastStart = tokenStart
			attrs := map[string]string{}
			for _, attr := range t.Attr {
				if attr.Name.Space == "xmlns" || attr.Name.Local == "xmlns" {
					continue
				}
				attrs[attr.Name.Local] = attr.Value
			}
			stack = append(stack, frame{
				space: t.Name.Space,
				local: t.Name.Local,
				attrs: attrs,
			})
			if t.Name.Space != TRXNS {
				continue
			}
			switch t.Name.Local {
			case "UnitTest":
				unitID, unitName = attrs["id"], attrs["name"]
			case "UnitTestResult":
				out.Results = append(out.Results, TRXResult{
					TestID:   attrs["testId"],
					TestName: attrs["testName"],
					Outcome:  attrs["outcome"],
					Duration: attrs["duration"],
				})
				current = len(out.Results) - 1
			case "Output":
				if current >= 0 {
					inOutput = true
				}
			}
		case xml.CharData:
			if len(stack) == 0 {
				continue
			}
			top := &stack[len(stack)-1]
			if !top.hasText {
				top.text = string(t)
				top.hasText = true
			}
		case xml.EndElement:
			if len(stack) == 0 {
				continue
			}
			finished := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			if finished.space != TRXNS {
				continue
			}
			switch finished.local {
			case "Times":
				out.Times = append(out.Times, TRXTimes{
					Creation: finished.attrs["creation"],
					Finish:   finished.attrs["finish"],
				})
			case "UnitTest":
				unitID, unitName = "", ""
			case "TestMethod":
				name := finished.attrs["name"]
				if name == "" {
					name = unitName
				}
				short := lastSegment(finished.attrs["className"])
				if short != "" {
					out.Owners[unitID] = short + "." + name
				} else {
					out.Owners[unitID] = name
				}
			case "UnitTestResult":
				current = -1
			case "Output":
				inOutput = false
			case "StdOut", "StdErr", "DebugTrace":
				if current < 0 || !inOutput || !finished.hasText {
					continue
				}
				out.Results[current].Output = append(out.Results[current].Output, OutputChunk{
					Tag:  finished.local,
					Text: finished.text,
				})
			}
		}
	}
	return out, nil
}

// lastSegment is the text after the last dot: the class without its
// namespace. The python originals wrote cls.rsplit(".", 1)[-1].
func lastSegment(text string) string {
	if idx := strings.LastIndexByte(text, '.'); idx >= 0 {
		return text[idx+1:]
	}
	return text
}

// xmlPosition is where the XML parser stopped: "line L, column C".
//
// The python originals printed the expat message. The shape is kept here:
// "unclosed token", "mismatched tag", "not well-formed (invalid token)" and
// "no element found" are the four messages expat produces, and the columns
// are 0-based, the way expat counts them.
func xmlPosition(data []byte, offset int64, err error, lastStart int64) string {
	switch {
	case err == nil:
		return fmt.Sprintf("no element found: %s", lineColumn(data, offset))
	case errors.Is(err, io.ErrUnexpectedEOF):
		if lastStart < 0 {
			return fmt.Sprintf("no element found: %s", lineColumn(data, offset))
		}
		return fmt.Sprintf("unclosed token: %s", lineColumn(data, lastStart))
	}
	var syntax *xml.SyntaxError
	if errors.As(err, &syntax) {
		if strings.Contains(syntax.Msg, "unexpected EOF") {
			if lastStart < 0 {
				return fmt.Sprintf("no element found: %s", lineColumn(data, offset))
			}
			return fmt.Sprintf("unclosed token: %s", lineColumn(data, lastStart))
		}
		if strings.Contains(syntax.Msg, "closed by") {
			return fmt.Sprintf("mismatched tag: %s", lineColumn(data, offset))
		}
	}
	return fmt.Sprintf("not well-formed (invalid token): %s", lineColumn(data, offset))
}

// lineColumn is the 1-based line and the 0-based column of a byte offset,
// the way expat counts them.
func lineColumn(data []byte, offset int64) string {
	if offset > int64(len(data)) {
		offset = int64(len(data))
	}
	if offset < 0 {
		offset = 0
	}
	line := 1
	column := 0
	for index := int64(0); index < offset; index++ {
		if data[index] == '\n' {
			line++
			column = 0
			continue
		}
		column++
	}
	return fmt.Sprintf("line %d, column %d", line, column)
}

// RawDirNames lists the names of a directory in the order the FILESYSTEM
// lists them.
//
// os.ReadDir sorts, and the python originals read the directories with
// os.walk, which does not. The order of the rendered tables depends on it,
// so the walk is done by hand.
func RawDirNames(path string) []string {
	dir, err := os.Open(path)
	if err != nil {
		return nil
	}
	names, err := dir.Readdirnames(-1)
	dir.Close()
	if err != nil {
		return nil
	}
	return names
}

// FindTRX lists the TRX files under the given targets.
//
// A target that is a FILE is taken as it is; a target that is a directory is
// walked. The order is the one the python originals produced: the files of
// one directory in the order the filesystem lists them, the directories in
// the order the filesystem lists them.
func FindTRX(targets []string, lowerCase bool) []string {
	var paths []string
	for _, target := range targets {
		info, err := os.Stat(target)
		if err == nil && !info.IsDir() {
			paths = append(paths, target)
			continue
		}
		walkDir(target, func(path string) {
			paths = append(paths, path)
		}, lowerCase)
	}
	return paths
}

// walkDir visits every .trx file under root, in the filesystem's own order.
//
// The python original used os.walk, which does not sort. Go's filepath.Walk
// does, and the difference is visible in the order of the rendered tables,
// so the walk is done by hand here: the files of one directory first, then
// the subdirectories, in the order the filesystem lists them.
func walkDir(root string, visit func(path string), lowerCase bool) {
	dir, err := os.Open(root)
	if err != nil {
		return
	}
	names, err := dir.Readdirnames(-1)
	dir.Close()
	if err != nil {
		return
	}
	var subdirs []string
	for _, name := range names {
		full := filepath.Join(root, name)
		info, err := os.Lstat(full)
		if err != nil {
			continue
		}
		if info.IsDir() {
			subdirs = append(subdirs, full)
			continue
		}
		candidate := name
		if lowerCase {
			candidate = strings.ToLower(candidate)
		}
		if strings.HasSuffix(candidate, ".trx") {
			visit(full)
		}
	}
	for _, sub := range subdirs {
		walkDir(sub, visit, lowerCase)
	}
}

// SortedFindTRX is FindTRX with the result sorted, the way
// test-measurements.py and test-census.py read their inputs.
func SortedFindTRX(targets []string) []string {
	paths := FindTRX(targets, true)
	sort.Strings(paths)
	return paths
}
