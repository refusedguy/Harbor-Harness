// Package checkxmldoccoverage is the compiled port of
// tools/check-xml-doc-coverage.py.
//
// The gate asks whether every public member of the frozen contract assembly
// carries an XML doc — or a named, justified suppression.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package checkxmldoccoverage

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/gatekit"
)

// scope is the frozen surface the gate reads.
//
// The scope is CONTRACTS ONLY: the frozen data surface is
// Harbor.Abstractions.Contracts, the layer every other assembly points at.
var scope = []string{"src/Harbor.Abstractions.Contracts"}

// suppressed is file-relative-path :: member-name -> why no XML doc (yet).
//
// Each key must match exactly one currently-undocumented member (enforced:
// XMLDOC-STALE-SUPPRESSION). Member names, not lines: line numbers in a
// snapshot are stale before the pull request lands.
var suppressed = map[string]string{
	"src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs::Timestamp":                            "base-record plumbing, type-level union doc applies",
	"src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs::AgentStartEvent":                      "payload-free envelope, union doc on AgentEvent applies",
	"src/Harbor.Abstractions.Contracts/Events/AgentEvent.cs::TextStartEvent":                       "payload-free envelope, union doc on LlmEvent applies",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::ChatRole":                               "legacy enum, values mirror the documented role model",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::User":                                   "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Assistant":                              "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Thinking":                               "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Tool":                                   "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::ToolResult":                             "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::System":                                 "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::Error":                                  "legacy enum value, see ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/ChatLine.cs::ChatLine":                               "legacy one-line struct, positional roles mirror ChatRole",
	"src/Harbor.Abstractions.Contracts/Models/Session.cs::StopReasonJsonConverter":                 "mechanical JsonConverter, documented via StopReasonTable",
	"src/Harbor.Abstractions.Contracts/Permissions/PatternRegexCache.cs::BoundedPatternRegexCache": "default-arg ctor, semantic stated on DefaultCapacity",
	"src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs::RegexCacheProvider":       "test seam passthrough, field doc above applies",
}

// typeKind is the kind of a type declaration.
const typeKind = `(class|record(?:\s+(?:struct|class))?|struct|interface|enum)`

// typeDecl is a type declaration with an access modifier.
var typeDecl = regexp.MustCompile(
	`^\s*(?:public|protected|internal|private)\s+` +
		`(?:(?:sealed|abstract|static|partial|readonly|ref)\s+)*` +
		typeKind + `\s+(\w+)`)

// implicitTypeDecl is a type declaration without an access modifier.
var implicitTypeDecl = regexp.MustCompile(
	`^\s*(?:(?:sealed|abstract|static|partial|readonly|ref)\s+)*` +
		typeKind + `\s+(\w+)`)

// enumValue is one value of an enum body.
var enumValue = regexp.MustCompile(`^\s*(\w+)\s*(?:=\s*[^,;]+)?,?\s*(?://.*)?$`)

// typeKeyword is the check that the raw line really holds a type.
var typeKeyword = regexp.MustCompile(`\b(class|record|struct|interface|enum)\b`)

// lineComment is a trailing comment.
var lineComment = regexp.MustCompile(`//.*$`)

// methodName is the name before a parameter list.
var methodName = regexp.MustCompile(`(\w+)\s*(?:<[^()]*>)?\s*\(`)

// wordRe finds the words of a line.
var wordRe = regexp.MustCompile(`\w+`)

// member is one gated member.
type member struct {
	relpath    string
	line       int
	name       string
	display    string
	documented bool
}

// Run is the gate entry point.
func Run(args []string) int {
	minMembers := 0
	minDocumented := 0
	minCoverage := 0.0
	selfTest := false
	report := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, value, hasValue := splitFlag(arg)
		switch name {
		case "--min-members":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-members needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-members needs a number")
				return gate.Broken
			}
			minMembers = parsed
		case "--min-documented":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-documented needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, ok := parseInt(value)
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-documented needs a number")
				return gate.Broken
			}
			minDocumented = parsed
		case "--min-coverage":
			if !hasValue {
				if i+1 >= len(args) {
					fmt.Fprintln(os.Stderr, "--min-coverage needs a number")
					return gate.Broken
				}
				i++
				value = args[i]
			}
			parsed, err := parseFloat(value)
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-coverage needs a number")
				return gate.Broken
			}
			minCoverage = parsed
		case "--self-test":
			selfTest = true
		case "--report":
			report = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-xml-doc-coverage [--min-members N] [--min-documented N] [--min-coverage F] [--report] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unrecognized argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	repo, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	if report {
		printReport(repo)
		return gate.Green
	}

	findings, total, documented, coverage, suppressedCount := runGate(repo, minMembers, minDocumented, minCoverage)
	fmt.Printf("compared %d public members (%d documented, %d suppressed, coverage %.1f%%) against the XML-doc gate in src/Harbor.Abstractions.Contracts\n",
		total, documented, suppressedCount, coverage*100)
	for _, item := range findings {
		fmt.Printf("FAIL %s:%d: %s\n", item.path, item.line, item.text)
	}
	if len(findings) > 0 {
		fmt.Printf("\n%d finding(s) — red\n", len(findings))
		return gate.Red
	}
	fmt.Println("OK: no unsuppressed undocumented members, no stale suppressions")
	return gate.Green
}

// finding is one problem of the gate.
type finding struct {
	path string
	line int
	text string
}

// splitFlag splits "--name=value" into its parts.
func splitFlag(arg string) (string, string, bool) {
	if !strings.HasPrefix(arg, "--") {
		return arg, "", false
	}
	if idx := strings.IndexByte(arg, '='); idx >= 0 {
		return arg[:idx], arg[idx+1:], true
	}
	return arg, "", false
}

// parseInt reads a decimal integer.
func parseInt(text string) (int, bool) {
	if text == "" {
		return 0, false
	}
	negative := false
	body := text
	if body[0] == '+' || body[0] == '-' {
		negative = body[0] == '-'
		body = body[1:]
	}
	if body == "" {
		return 0, false
	}
	value := 0
	for _, r := range body {
		if r < '0' || r > '9' {
			return 0, false
		}
		value = value*10 + int(r-'0')
	}
	if negative {
		value = -value
	}
	return value, true
}

// parseFloat reads a decimal number.
func parseFloat(text string) (float64, error) {
	return strconv.ParseFloat(text, 64)
}

// stripLineNoise removes string literals so braces inside them cannot move
// the depth.
func stripLineNoise(line string) string {
	var out strings.Builder
	inString := false
	index := 0
	for index < len(line) {
		c := line[index]
		if inString {
			if c == '\\' {
				index += 2
				continue
			}
			if c == '"' {
				inString = false
			}
			index++
			continue
		}
		if c == '"' {
			inString = true
			index++
			continue
		}
		out.WriteByte(c)
		index++
	}
	return lineComment.ReplaceAllString(out.String(), "")
}

// memberNameFromDecl is the simple name of a public/protected declaration
// line (first line only).
//
// Initializers and bodies are cut at the first `;`, `{` or `=` outside
// parentheses, so `= new(...)` / `=> ...` tails cannot donate a name (that
// shape once yielded "new" for a property).
func memberNameFromDecl(stripped string) string {
	depth := 0
	cut := -1
	for index := 0; index < len(stripped); index++ {
		ch := stripped[index]
		switch ch {
		case '(':
			depth++
		case ')':
			depth--
		case ';', '{', '=':
			if depth == 0 {
				cut = index
			}
		}
		if cut >= 0 {
			break
		}
	}
	head := stripped
	if cut >= 0 {
		head = stripped[:cut]
	}
	if strings.Contains(head, "operator") {
		after := strings.TrimSpace(strings.SplitN(head, "operator", 2)[1])
		if after == "" {
			return "operator"
		}
		return "operator " + strings.Fields(after)[0]
	}
	match := methodName.FindStringSubmatch(head)
	if match != nil {
		switch match[1] {
		case "public", "protected", "private", "internal", "static", "sealed",
			"abstract", "partial", "readonly", "override", "virtual", "async",
			"new", "extern", "unsafe":
		default:
			return match[1]
		}
	}
	// The identifier before `(` was a modifier/keyword (e.g. a `new(...)`
	// tail that survived the cut): fall through to the last word.
	tokens := wordRe.FindAllString(head, -1)
	if len(tokens) > 0 {
		return tokens[len(tokens)-1]
	}
	return strings.TrimSpace(head)
}

// iterMembers is (line_no, name, display) for every gated member in one file.
func iterMembers(lines []string) []member {
	var members []member
	depth := 0
	var stack []string
	pendingKind := ""
	for index, raw := range lines {
		lineno := index + 1
		stripped := strings.TrimSpace(raw)
		code := strings.TrimSpace(stripLineNoise(raw))

		match := typeDecl.FindStringSubmatch(raw)
		if match == nil {
			match = implicitTypeDecl.FindStringSubmatch(code)
		}
		if match != nil && typeKeyword.MatchString(raw) {
			kind, name := match[1], match[2]
			if strings.HasPrefix(stripped, "public ") || strings.HasPrefix(stripped, "protected ") {
				members = append(members, member{line: lineno, name: name, display: stripped})
			}
			pendingKind = kind
		} else if strings.HasPrefix(code, "public ") || strings.HasPrefix(code, "protected ") {
			// A public/protected declaration line. `namespace`/`using` never
			// carry an access modifier, so no exclusion is needed.
			members = append(members, member{line: lineno, name: memberNameFromDecl(stripped), display: stripped})
		} else if len(stack) > 0 && stack[len(stack)-1] == "interface" {
			// Implicitly-public interface member (no access modifier). Only
			// shapes with a parameter list or an accessor block qualify.
			if stripped != "" && !startsWithAny(stripped, "[", "/", "#", "{", "}", "using ", "namespace ") &&
				(interfaceShapeRe.MatchString(code)) {
				members = append(members, member{line: lineno, name: memberNameFromDecl(stripped), display: stripped})
			}
		} else if len(stack) > 0 && stack[len(stack)-1] == "enum" {
			enumMatch := enumValue.FindStringSubmatch(raw)
			if enumMatch != nil && !startsWithAny(stripped, "[", "/", "#", "{", "}", "using ") {
				members = append(members, member{line: lineno, name: enumMatch[1], display: stripped})
			}
		}

		opens := strings.Count(code, "{")
		closes := strings.Count(code, "}")
		if pendingKind != "" {
			if opens > 0 {
				stack = append(stack, pendingKind)
				pendingKind = ""
			} else if strings.Contains(code, ";") {
				pendingKind = ""
			}
		}
		depth += opens - closes
		for len(stack) > depth {
			stack = stack[:len(stack)-1]
		}
		if depth < 0 {
			depth = 0
		}
	}
	return members
}

// interfaceShapeRe is the shape of an implicitly-public interface member.
var interfaceShapeRe = regexp.MustCompile(`\(|\bget\b|\bset\b`)

// startsWithAny is the python str.startswith over a tuple.
func startsWithAny(text string, prefixes ...string) bool {
	for _, prefix := range prefixes {
		if strings.HasPrefix(text, prefix) {
			return true
		}
	}
	return false
}

// hasDoc is true when /// docs precede lines[idx] (skipping blanks and
// attributes).
func hasDoc(lines []string, index int) bool {
	j := index - 1
	for j >= 0 && (strings.TrimSpace(lines[j]) == "" || strings.HasPrefix(strings.TrimSpace(lines[j]), "[")) {
		j--
	}
	return j >= 0 && strings.HasPrefix(strings.TrimSpace(lines[j]), "///")
}

// checkTree scans the scope. It returns (members, undocumented, stale).
func checkTree(repo string) ([]member, []member, []staleSuppression) {
	var members []member
	for _, directory := range scope {
		var paths []string
		root := filepath.Join(repo, directory)
		filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			paths = append(paths, path)
			return nil
		})
		sort.Strings(paths)
		for _, path := range paths {
			relpath, err := filepath.Rel(repo, path)
			if err != nil {
				continue
			}
			relpath = filepath.ToSlash(relpath)
			data, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			lines := gatekit.SplitLines(string(data))
			for _, item := range iterMembers(lines) {
				item.relpath = relpath
				item.documented = hasDoc(lines, item.line-1)
				members = append(members, item)
			}
		}
	}

	var undocumented []member
	for _, item := range members {
		if !item.documented {
			undocumented = append(undocumented, item)
		}
	}

	// Each suppression must match exactly one undocumented member.
	hits := map[string]int{}
	for _, item := range undocumented {
		hits[item.relpath+"::"+item.name]++
	}
	var stale []staleSuppression
	for _, key := range sortedKeys(suppressed) {
		if hits[key] != 1 {
			stale = append(stale, staleSuppression{key: key, count: hits[key]})
		}
	}
	return members, undocumented, stale
}

// staleSuppression is a suppression that does not match exactly one member.
type staleSuppression struct {
	key   string
	count int
}

// sortedKeys are the keys of a map, in order.
func sortedKeys(values map[string]string) []string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

// runGate is the gate itself.
func runGate(repo string, minMembers int, minDocumented int, minCoverage float64) ([]finding, int, int, float64, int) {
	members, undocumented, stale := checkTree(repo)
	total := len(members)
	documented := total - len(undocumented)
	coverage := 0.0
	if total > 0 {
		coverage = float64(documented) / float64(total)
	}
	var findings []finding
	for _, item := range undocumented {
		if _, ok := suppressed[item.relpath+"::"+item.name]; !ok {
			findings = append(findings, finding{
				path: item.relpath,
				line: item.line,
				text: fmt.Sprintf("XMLDOC-UNDOCUMENTED %s: no /// doc and no SUPPRESSED entry — add a doc or a suppression with reason", item.name),
			})
		}
	}
	for _, item := range stale {
		findings = append(findings, finding{
			path: "tools/check-xml-doc-coverage.py",
			line: 0,
			text: fmt.Sprintf("XMLDOC-STALE-SUPPRESSION %s: matches %d undocumented members, want exactly 1 — drop it (docs landed) or fix the key",
				item.key, item.count),
		})
	}
	if total < minMembers {
		findings = append(findings, finding{
			path: "tools/check-xml-doc-coverage.py",
			line: 0,
			text: fmt.Sprintf("XMLDOC-FLOOR members %d < --min-members %d — the matcher sees less of the tree than it should",
				total, minMembers),
		})
	}
	if documented < minDocumented {
		findings = append(findings, finding{
			path: "tools/check-xml-doc-coverage.py",
			line: 0,
			text: fmt.Sprintf("XMLDOC-FLOOR documented %d < --min-documented %d", documented, minDocumented),
		})
	}
	if coverage < minCoverage {
		findings = append(findings, finding{
			path: "tools/check-xml-doc-coverage.py",
			line: 0,
			text: fmt.Sprintf("XMLDOC-FLOOR coverage %.3f < --min-coverage %s", coverage, gatekit.PyFloat(minCoverage)),
		})
	}
	suppressedCount := 0
	for _, item := range undocumented {
		if _, ok := suppressed[item.relpath+"::"+item.name]; ok {
			suppressedCount++
		}
	}
	return findings, total, documented, coverage, suppressedCount
}

// printReport is the per-file member table.
func printReport(repo string) {
	members, undocumented, _ := checkTree(repo)
	gaps := map[string]bool{}
	for _, item := range undocumented {
		gaps[item.relpath+"::"+item.name] = true
	}
	byFile := map[string][]member{}
	for _, item := range members {
		byFile[item.relpath] = append(byFile[item.relpath], item)
	}
	for _, relpath := range sortedMemberFiles(byFile) {
		items := byFile[relpath]
		sort.SliceStable(items, func(i, j int) bool { return items[i].line < items[j].line })
		for _, item := range items {
			mark := "doc "
			if !item.documented {
				if _, ok := suppressed[relpath+"::"+item.name]; ok && gaps[relpath+"::"+item.name] {
					mark = "supp"
				} else {
					mark = "GAP "
				}
			}
			fmt.Printf("%s %s:%d: %s\n", mark, relpath, item.line, item.name)
		}
	}
	total := len(members)
	documented := 0
	for _, item := range members {
		if item.documented {
			documented++
		}
	}
	suppressedCount := 0
	for _, item := range undocumented {
		if _, ok := suppressed[item.relpath+"::"+item.name]; ok {
			suppressedCount++
		}
	}
	share := 0.0
	if total > 0 {
		share = float64(documented) / float64(total) * 100
	}
	fmt.Printf("\n%d members, %d documented (%.1f%%), %d suppressed\n",
		total, documented, share, suppressedCount)
}

// sortedMemberFiles are the files of the report, in order.
func sortedMemberFiles(byFile map[string][]member) []string {
	files := make([]string, 0, len(byFile))
	for path := range byFile {
		files = append(files, path)
	}
	sort.Strings(files)
	return files
}
