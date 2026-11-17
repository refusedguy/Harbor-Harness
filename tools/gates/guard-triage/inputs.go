package guardtriage

// inputs.go -- where the firings come from: a TRX file, or a CI job log.

import (
	"encoding/xml"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// trxNamespace is the namespace of the test result format.
const trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

// xmlNode is one element of a TRX document.
//
// `text` is the text BEFORE the first child, the way python's
// ElementTree's `.text` is: the assertion message of a `<Message>` element
// is that element's own text, and the element's children hold the rest.
type xmlNode struct {
	name     string
	ns       string
	attrs    map[string]string
	text     string
	children []*xmlNode
}

// localName is the name without the namespace.
func (n *xmlNode) localName() string { return n.name }

// attribute reads an attribute.
func (n *xmlNode) attribute(key string) string { return n.attrs[key] }

// parseTRX reads a TRX document.
func parseTRX(data []byte) (*xmlNode, error) {
	decoder := xml.NewDecoder(strings.NewReader(string(data)))
	var root *xmlNode
	var stack []*xmlNode
	for {
		tok, err := decoder.Token()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, err
		}
		switch t := tok.(type) {
		case xml.StartElement:
			node := &xmlNode{name: t.Name.Local, ns: t.Name.Space, attrs: map[string]string{}}
			for _, attr := range t.Attr {
				node.attrs[attr.Name.Local] = attr.Value
			}
			if len(stack) > 0 {
				parent := stack[len(stack)-1]
				parent.children = append(parent.children, node)
				// The text before the first child is the element's text.
				parent.text = ""
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
				if len(current.children) == 0 {
					current.text += string(t)
				}
			}
		}
	}
	if root == nil {
		return nil, io.EOF
	}
	return root, nil
}

// descendants returns every descendant with the local name, in document
// order.
func (n *xmlNode) descendants(local string, namespace string, out *[]*xmlNode) {
	for _, child := range n.children {
		if child.localName() == local &&
			(child.ns == "" || child.ns == namespace || namespace == "") {
			*out = append(*out, child)
		}
		child.descendants(local, namespace, out)
	}
}

// trxText is the text of the first descendant with this local name.
func trxText(n *xmlNode, tag string) string {
	if n.localName() == tag {
		return strings.TrimSpace(n.text)
	}
	for _, child := range n.children {
		if found := trxText(child, tag); found != "" {
			return found
		}
	}
	return ""
}

// firingsFromTRX returns one firing per failed test in a TRX file.
func firingsFromTRX(path string) []firing {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	root, err := parseTRX(data)
	if err != nil {
		return nil
	}
	var results []*xmlNode
	root.descendants("UnitTestResult", trxNamespace, &results)
	if len(results) == 0 {
		root.descendants("UnitTestResult", "", &results)
	}

	var out []firing
	for _, result := range results {
		if !strings.EqualFold(result.attribute("outcome"), "failed") {
			continue
		}
		testName := result.attribute("testName")
		className := result.attribute("className")
		message := trxText(result, "Message")
		stack := trxText(result, "StackTrace")
		// The tag is the python original's, "Output/StdOut". The reader
		// compares a LOCAL name against it, and no element's local name
		// is "Output/StdOut" — so the stdout is never part of the
		// evidence. The port asks for the same tag and gets the same
		// answer, because the answer is what the report says.
		stdout := trxText(result, "Output/StdOut")
		evidence := strings.TrimSpace(strings.Join(nonEmpty(message, stdout), "\n"))
		clsName, method := splitTestName(testName, className)
		rule := testName
		if clsName != "" {
			rule = clsName + "." + method
		}
		if m := stackFrame.FindStringSubmatch(stack); m != nil {
			rule = ruleFromFrame(m[1], m[2], m[3])
		}
		out = append(out, firing{
			rule:      rule,
			testName:  testName,
			project:   projectOf(className, clsName),
			evidence:  evidence,
			source:    "trx",
			origin:    path,
			locations: extractLocations(evidence),
		})
	}
	return out
}

// nonEmpty joins the parts that are not empty.
func nonEmpty(parts ...string) []string {
	var out []string
	for _, part := range parts {
		if part != "" {
			out = append(out, part)
		}
	}
	return out
}

// firingsFromLog returns one firing per failed test block in a CI job log.
func firingsFromLog(path string) []firing {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	raw := string(data)
	project := ""
	var lines []string
	for _, line := range splitLines(raw) {
		lines = append(lines, cleanLine(line))
	}

	var out []firing
	index := 0
	for index < len(lines) {
		line := lines[index]
		if m := projectGroup.FindStringSubmatch(line); m != nil {
			project = m[1]
		}
		var match []string
		if strings.Contains(line, "failed") {
			match = failedTest.FindStringSubmatch(line)
		}
		if match == nil || strings.Contains(line, "failed:") {
			index++
			continue
		}
		testName := match[1]
		var block []string
		index++
		for index < len(lines) && len(block) < maxEvidenceLine {
			next := lines[index]
			if strings.Contains(next, "failed") && failedTest.MatchString(next) &&
				!strings.Contains(next, "failed:") {
				break
			}
			if strings.HasPrefix(next, "[slow]") ||
				strings.HasPrefix(next, "In process file artifacts") {
				break
			}
			if strings.TrimSpace(next) != "" {
				block = append(block, next)
			}
			index++
		}
		evidence := strings.TrimSpace(strings.Join(block, "\n"))
		var frame []string
		for _, candidate := range block {
			frame = stackFrame.FindStringSubmatch(candidate)
			if frame != nil {
				break
			}
		}
		clsName := ""
		if strings.Contains(testName, ".") {
			parts := strings.Split(testName, ".")
			clsName = parts[len(parts)-2]
		}
		method := testName
		if i := strings.LastIndex(method, "."); i >= 0 {
			method = method[i+1:]
		}
		rule := testName
		if clsName != "" {
			rule = clsName + "." + method
		}
		if frame != nil {
			rule = ruleFromFrame(frame[1], frame[2], frame[3])
			// The stack frame names the assembly the rule lives in.
			if project == "" && strings.HasSuffix(frame[1], ".Tests") {
				project = frame[1]
			}
		}
		out = append(out, firing{
			rule:      rule,
			testName:  testName,
			project:   project,
			evidence:  evidence,
			source:    "log",
			origin:    path,
			locations: extractLocations(evidence),
		})
	}
	return out
}

// cleanLine is a log line without its colour codes, timestamp and logger
// prefix.
func cleanLine(line string) string {
	text := ansi.ReplaceAllString(line, "")
	text = ghPrefix.ReplaceAllString(text, "")
	text = nukePrefix.ReplaceAllString(text, "")
	return strings.TrimRight(text, " \t\n\v\f\r")
}

// collectTRX reads the firings out of the TRX files the paths name.
func collectTRX(paths []string) ([]firing, []string) {
	var firings []firing
	var used []string
	for _, raw := range paths {
		for _, file := range expand(raw, "*.trx") {
			found := firingsFromTRX(file)
			if len(found) > 0 {
				used = append(used, file)
				firings = append(firings, found...)
			}
		}
	}
	return firings, used
}

// collectLogs reads the firings out of the log files the paths name.
func collectLogs(paths []string) ([]firing, []string) {
	var firings []firing
	var used []string
	for _, raw := range paths {
		for _, file := range expand(raw, "*.log") {
			found := firingsFromLog(file)
			if len(found) > 0 {
				used = append(used, file)
				firings = append(firings, found...)
			}
		}
	}
	return firings, used
}

// expand is a path, a directory or a glob pattern -> the files to read.
func expand(raw string, pattern string) []string {
	if strings.ContainsAny(raw, "*?[") {
		matches, err := filepath.Glob(raw)
		if err != nil {
			return nil
		}
		var out []string
		for _, match := range matches {
			if info, err := os.Stat(match); err == nil && info.Mode().IsRegular() {
				out = append(out, match)
			}
		}
		sort.Strings(out)
		return out
	}
	info, err := os.Stat(raw)
	if err != nil {
		return nil
	}
	if info.IsDir() {
		var out []string
		filepath.WalkDir(raw, func(path string, entry os.DirEntry, err error) error {
			if err != nil {
				return nil
			}
			if !entry.IsDir() {
				if matched, _ := filepath.Match(pattern, entry.Name()); matched {
					out = append(out, path)
				}
			}
			return nil
		})
		sort.Strings(out)
		return out
	}
	return []string{raw}
}
