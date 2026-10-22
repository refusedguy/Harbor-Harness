// Package codemodtoolnames is the compiled port of tools/codemod-tool-names.py.
//
// The codemod moves the tool-name string literals in `src/` and `apps/` onto
// the WellKnownTools catalog, so a builtin tool's name is written down once.
// It is a tool a human runs: no workflow runs it, and the CI_GATES table
// says so.
//
// The python original was deleted in the same change. A python original and
// a compiled twin of the same tool never coexist, and the twin is how a
// drifted pair is caught.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the tool
// could not run.
package codemodtoolnames

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The catalog, read from the tree.
const (
	catalogRel      = "src/Harbor.Abstractions.Contracts/Models/Identifiers/WellKnownTools.cs"
	catalogType     = "WellKnownTools"
	catalogUsing    = "using Harbor.Abstractions.Models.Identifiers;"
	catalogNS       = "Harbor.Abstractions.Models.Identifiers"
	catalogTypeDecl = `public\s+static\s+class\s+WellKnownTools`
)

var (
	catalogRowRe = regexp.MustCompile(
		`public\s+static\s+readonly\s+ToolName\s+(?P<member>\w+)\s*=\s*` +
			`ToolName\.Create\(\s*"(?P<name>[a-z][a-z0-9_]*)"\s*\)`)
	catalogTypeRe = regexp.MustCompile(catalogTypeDecl)
)

// The roots the tool reads.
//
// `tests/` is deliberately NOT among them: a test that names a tool is a
// test ASSERTING a name, and rewriting it to a constant would make the test
// agree with the product by construction instead of by measurement.
//
// `samples/` is audited but NOT rewritten: a CS-source plugin is compiled at
// load time against a FIXED set of injected namespaces, and the catalog's
// namespace is not among them, so a rewritten sample would fail to compile
// when the plugin loads.
var (
	rewriteRoots = []string{"src", "apps"}
	auditRoots   = []string{"src", "apps", "samples"}
)

// The build output, which is never a source file.
var skipDirs = map[string]bool{
	"bin": true, "obj": true, ".git": true, "out": true, "packages": true,
}

// catalog is the name -> member map, read from the catalog file.
//
// The order matters: python's dict keeps the order the file declared the
// names in, and the audit prints the names in SORTED order but the rewrite
// reports them in the order the rules found them.
type catalog struct {
	order  []string
	member map[string]string
}

func (c *catalog) has(name string) bool {
	_, ok := c.member[name]
	return ok
}

func (c *catalog) sortedNames() []string {
	out := append([]string{}, c.order...)
	sort.Strings(out)
	return out
}

// readCatalog reads the name -> member map out of the catalog file.
func readCatalog(repo string) (*catalog, error) {
	path := filepath.Join(repo, filepath.FromSlash(catalogRel))
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("catalog not found: %s", path)
	}
	text := string(data)
	if !catalogTypeRe.MatchString(text) {
		return nil, fmt.Errorf("no `public static class WellKnownTools` in %s", path)
	}
	cat := &catalog{member: map[string]string{}}
	for _, m := range catalogRowRe.FindAllStringSubmatch(text, -1) {
		name, member := m[2], m[1]
		if _, seen := cat.member[name]; !seen {
			cat.order = append(cat.order, name)
		}
		cat.member[name] = member
	}
	if len(cat.order) == 0 {
		return nil, fmt.Errorf("the catalog declares no tool names: %s", path)
	}
	return cat, nil
}

// rule is one rewrite rule.
//
// Each rule says where a literal IS a tool name, and a literal that is
// something else — a JSON argument key, a CLI verb, a display label, prose
// in a doc comment — is left exactly where it is.
type rule struct {
	id       string
	prefixes []string
	pattern  *regexp.Regexp
	template string
	// setRule is the one rule that rewrites a SET of names, which is not a
	// plain substitution: every element must be a catalog name, or the whole
	// set is left alone.
	setRule bool
}

// The rules, in the order the python original applied them.
func rules() []rule {
	return []rule{
		{
			id: "permission-rule",
			prefixes: []string{
				"src/Harbor.Abstractions/Agents/AgentDefinition.cs",
				"src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs",
			},
			// new("read", "*", PermissionAction.Allow) — the first argument
			// of a permission rule is the tool the rule is about.
			pattern:  regexp.MustCompile(`new\(\s*"(?P<name>[a-z][a-z0-9_]*)"\s*,\s*`),
			template: "new({member}, ",
		},
		{
			id: "safety-declaration",
			prefixes: []string{
				"src/Harbor.Abstractions.Contracts/Permissions/BuiltinToolSafetyProfiles.cs",
			},
			// new("write", ToolSafetyProfile.Path()) — the safety table's
			// tool column.
			pattern:  regexp.MustCompile(`new\(\s*"(?P<name>[a-z][a-z0-9_]*)"\s*,\s*ToolSafetyProfile\.`),
			template: "new({member}, ToolSafetyProfile.",
		},
		{
			id:       "tool-error-handler",
			prefixes: []string{"src/Harbor.Tools.Builtin"},
			// ToolErrors.Handler("bash", ct) — the tool whose error is
			// being shaped.
			pattern:  regexp.MustCompile(`ToolErrors\.Handler\(\s*"(?P<name>[a-z][a-z0-9_]*)"`),
			template: "ToolErrors.Handler({member}",
		},
		{
			id: "tool-name-comparison",
			prefixes: []string{
				"src/Harbor.Ui.Framework.State/Diff/DiffPreview.cs",
			},
			// toolName != "edit" — a comparison against a tool name.
			pattern:  regexp.MustCompile(`\btoolName\s*(?P<op>==|!=)\s*"(?P<name>[a-z][a-z0-9_]*)"`),
			template: "toolName {op} {member}",
		},
		{
			id: "tool-name-set",
			prefixes: []string{
				"src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs",
				"src/Harbor.Abstractions.Contracts/Permissions/IArgSafetyPolicy.cs",
			},
			// new[] { "edit", "write" } — a set of tool names.
			pattern: regexp.MustCompile(`new\[\]\s*\{(?P<body>[^{}]*?)\}`),
			setRule: true,
		},
		{
			id: "ordinal-equals",
			prefixes: []string{
				"src/Harbor.Tui.CellForge/Chat/Widgets/ToolCallBlock.cs",
				"src/Harbor.Tui.CellForge/Chat/Widgets/ReadGroupBlock.cs",
			},
			// string.Equals(toolName, "bash", StringComparison.Ordinal)
			pattern: regexp.MustCompile(
				`string\.Equals\((?P<lhs>[^,()]+),\s*"(?P<name>[a-z][a-z0-9_]*)"\s*,\s*StringComparison\.`),
			template: "string.Equals({lhs}, {member}, StringComparison.",
		},
		{
			id: "const-tool-name",
			prefixes: []string{
				"src/Harbor.Tui.CellForge/Chat/Streaming/ToolCardTracker.cs",
				"src/Harbor.Terminal.Pty/PermissionGatedTerminalPaneLauncher.cs",
			},
			// private const string ShellPermission = "bash";
			pattern: regexp.MustCompile(
				`(?P<mods>(?:public|internal|private|protected)\s+)const\s+string\s+` +
					`(?P<id>\w+)\s*=\s*"(?P<name>[a-z][a-z0-9_]*)"\s*;`),
			template: "{mods}static readonly ToolName {id} = {member};",
		},
		{
			id:       "tool-result-entry",
			prefixes: []string{"src/Harbor.AgentRuntime"},
			// new ToolResultEntry(id, "task", output, isError)
			pattern: regexp.MustCompile(
				`new\s+ToolResultEntry\((?P<lhs>[^,()]+),\s*"(?P<name>[a-z][a-z0-9_]*)"\s*,`),
			template: "new ToolResultEntry({lhs}, {member},",
		},
		{
			id:       "demo-tool-call",
			prefixes: []string{"apps/Harbor.App.Cli"},
			// DemoReply.FromToolCall("bash", """...""")
			pattern:  regexp.MustCompile(`FromToolCall\(\s*"(?P<name>[a-z][a-z0-9_]*)"`),
			template: "FromToolCall({member}",
		},
		{
			id:       "tool-name-create",
			prefixes: []string{"src", "apps"},
			// ToolName.Create("tree") — the one construction site that is
			// left outside the catalog, which is what the catalog is for.
			pattern:  regexp.MustCompile(`ToolName\.Create\(\s*"(?P<name>[a-z][a-z0-9_]*)"\s*\)`),
			template: "{member}",
		},
	}
}

// hit is one literal a rule rewrote.
type hit struct {
	rule string
	name string
}

// inFileSet whether a repo-relative path is in the rule's file set.
//
// An empty file set applies to every product file.
func inFileSet(rel string, prefixes []string) bool {
	if len(prefixes) == 0 {
		return true
	}
	for _, prefix := range prefixes {
		if rel == prefix || strings.HasPrefix(rel, strings.TrimSuffix(prefix, "/")+"/") {
			return true
		}
	}
	return false
}

// rewrite applies every rule to one file.
//
// The mask is recomputed per rule: a rule's rewrite changes the text, so
// the offsets the previous mask was built on no longer line up.
func rewrite(text string, rel string, cat *catalog) (string, []hit) {
	var applied []hit
	for _, r := range rules() {
		if !inFileSet(rel, r.prefixes) {
			continue
		}
		mask := codeMask(text)
		if r.setRule {
			newText, hits := rewriteSet(text, cat, mask)
			text = newText
			applied = append(applied, hits...)
			continue
		}
		newText, hits := substitute(text, r, cat, mask)
		text = newText
		applied = append(applied, hits...)
	}
	text = explicitConditionalConversion(text)
	return text, applied
}

// substitute replaces every match of the rule's pattern with the template.
//
// The template is a python format string in the original; here it is a
// `{name}` placeholder the group values fill in.
func substitute(text string, r rule, cat *catalog, mask []bool) (string, []hit) {
	var applied []hit
	var b strings.Builder
	last := 0
	for _, idx := range r.pattern.FindAllStringSubmatchIndex(text, -1) {
		name := groupValue(text, r.pattern, idx, "name")
		start := idx[0]
		if name == "" || !cat.has(name) || !mask[start] {
			continue
		}
		fields := map[string]string{}
		for _, gname := range r.pattern.SubexpNames() {
			if gname == "" {
				continue
			}
			fields[gname] = groupValue(text, r.pattern, idx, gname)
		}
		fields["member"] = catalogType + "." + cat.member[name]
		applied = append(applied, hit{r.id, name})
		b.WriteString(text[last:start])
		b.WriteString(formatTemplate(r.template, fields))
		last = idx[1]
	}
	if len(applied) == 0 {
		return text, applied
	}
	b.WriteString(text[last:])
	return b.String(), applied
}

// groupValue is the text of a named group, or "" when the group did not
// match — python's groupdict maps an unmatched optional group to None, and
// the template treats it as an empty string.
func groupValue(text string, re *regexp.Regexp, idx []int, name string) string {
	n := re.SubexpIndex(name)
	if n < 0 || 2*n+1 >= len(idx) || idx[2*n] < 0 {
		return ""
	}
	return text[idx[2*n]:idx[2*n+1]]
}

// formatTemplate fills the `{name}` placeholders of a template.
func formatTemplate(template string, fields map[string]string) string {
	var b strings.Builder
	for i := 0; i < len(template); {
		if template[i] == '{' {
			end := strings.IndexByte(template[i:], '}')
			if end > 0 {
				key := template[i+1 : i+end]
				if value, ok := fields[key]; ok {
					b.WriteString(value)
					i += end + 1
					continue
				}
			}
		}
		b.WriteByte(template[i])
		i++
	}
	return b.String()
}

// rewriteSet rewrites `new[] { "a", "b" }` into `new string[] { Member.A, Member.B }`.
//
// Every element must be a catalog name: a set that mixes a tool name with
// something else is not a tool set, and rewriting half of it would change
// what the set means.
func rewriteSet(text string, cat *catalog, mask []bool) (string, []hit) {
	var applied []hit
	re := regexp.MustCompile(`new\[\]\s*\{(?P<body>[^{}]*?)\}`)
	nameRe := regexp.MustCompile(`"([a-z][a-z0-9_]*)"`)
	stripRe := regexp.MustCompile(`"[a-z][a-z0-9_]*"\s*,?\s*`)

	var b strings.Builder
	last := 0
	for _, idx := range re.FindAllStringSubmatchIndex(text, -1) {
		body := text[idx[2]:idx[3]]
		names := nameRe.FindAllStringSubmatch(body, -1)
		if len(names) == 0 {
			continue
		}
		allCatalog := true
		for _, n := range names {
			if !cat.has(n[1]) {
				allCatalog = false
				break
			}
		}
		if !allCatalog {
			continue
		}
		if strings.TrimSpace(stripRe.ReplaceAllString(body, "")) != "" {
			continue
		}
		if !mask[idx[0]] {
			continue
		}
		elements := make([]string, 0, len(names))
		for _, n := range names {
			applied = append(applied, hit{"tool-name-set", n[1]})
			elements = append(elements, catalogType+"."+cat.member[n[1]])
		}
		b.WriteString(text[last:idx[0]])
		b.WriteString("new string[] { " + strings.Join(elements, ", ") + " }")
		last = idx[1]
	}
	if len(applied) == 0 {
		return text, applied
	}
	b.WriteString(text[last:])
	return b.String(), applied
}

// typedDeclRe finds the `static readonly ToolName X =` declarations, whose
// names the conditional conversion below has to make explicit.
var typedDeclRe = regexp.MustCompile(`static\s+readonly\s+ToolName\s+(?P<id>\w+)\s*=`)

// explicitConditionalConversion makes the ToolName -> string conversion
// explicit in a conditional.
//
// `cond ? someString : typedField` is legal — a `ToolName` converts to
// `string` implicitly, and the conditional takes the string branch — but the
// reader has to know that to follow it. The conversion is written out
// instead, so the line says what it does.
//
// The python original expressed the "is it already converted?" test with a
// NEGATIVE LOOKAHEAD, which RE2 does not have. The test is made by hand
// here: the text after the name must not be `.Value`, with optional
// whitespace in between.
func explicitConditionalConversion(text string) string {
	var typed []string
	seen := map[string]bool{}
	for _, m := range typedDeclRe.FindAllStringSubmatch(text, -1) {
		if !seen[m[1]] {
			seen[m[1]] = true
			typed = append(typed, m[1])
		}
	}
	sort.Strings(typed)
	for _, name := range typed {
		text = convertConditional(text, name)
	}
	return text
}

// convertConditional writes `name.Value` where the name sits in the true
// branch of a conditional and is not converted already.
func convertConditional(text string, name string) string {
	var sites []int
	pos := 0
	for {
		idx := strings.Index(text[pos:], name)
		if idx < 0 {
			break
		}
		start := pos + idx
		end := start + len(name)
		// The python pattern ended with a word boundary, so a name that is
		// part of a longer identifier is not a match.
		if end < len(text) && isWordByte(text[end]) {
			pos = end
			continue
		}
		// The negative lookahead: the name is already converted.
		if followsValue(text[end:]) {
			pos = end
			continue
		}
		if _, ok := conditionalHead(text, start); !ok {
			pos = end
			continue
		}
		sites = append(sites, start)
		pos = end
	}
	if len(sites) == 0 {
		return text
	}
	var b strings.Builder
	last := 0
	for _, start := range sites {
		end := start + len(name)
		b.WriteString(text[last:start])
		b.WriteString(text[start:end])
		b.WriteString(".Value")
		last = end
	}
	b.WriteString(text[last:])
	return b.String()
}

// followsValue whether the text starts with the optional whitespace and
// then `.Value`.
func followsValue(rest string) bool {
	i := 0
	for i < len(rest) && (rest[i] == ' ' || rest[i] == '\t' || rest[i] == '\n' || rest[i] == '\r') {
		i++
	}
	return strings.HasPrefix(rest[i:], ".Value")
}

// conditionalHead returns the start of the `? ... :` head that ends right
// before the name, or false when there is none.
//
// The python pattern was `\?[^;\n]*?:\s*` — a `?`, then anything that is
// not a semicolon or a newline, lazily, then a `:`, then whitespace. The
// head therefore ends at the colon and the whitespace that follows it, and
// the `?` is the nearest one before it that no semicolon or newline
// separates it from.
func conditionalHead(text string, nameStart int) (int, bool) {
	// The whitespace run right before the name.
	ws := nameStart
	for ws > 0 && isSpaceByte(text[ws-1]) {
		ws--
	}
	if ws == 0 || text[ws-1] != ':' {
		return 0, false
	}
	colon := ws - 1
	for q := colon - 1; q >= 0; q-- {
		if text[q] == ';' || text[q] == '\n' {
			return 0, false
		}
		if text[q] == '?' {
			return q, true
		}
	}
	return 0, false
}

func isWordByte(c byte) bool {
	return c == '_' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
}

func isSpaceByte(c byte) bool {
	switch c {
	case ' ', '\t', '\n', '\v', '\f', '\r':
		return true
	}
	return false
}

// codeMask is a per-BYTE flag: is this offset inside a COMMENT?
//
// A literal inside a doc comment is prose about the code, not the code, and
// rewriting it would change documentation. The scanner tracks string and
// char literals so that a `//` inside a string (a URL, a path) does not
// start a comment.
//
// The python original masked CHARACTER by character and asked the mask with
// a character offset; this masks byte by byte and asks it with a byte
// offset. The two agree, because the mask is only ever asked about the
// FIRST byte of a match, and a multi-byte character is flagged as a whole.
func codeMask(text string) []bool {
	mask := make([]bool, len(text))
	for i := range mask {
		mask[i] = true
	}
	const (
		stateCode = iota
		stateLine
		stateBlock
		stateString
		stateChar
	)
	state := stateCode
	i := 0
	n := len(text)
	for i < n {
		c := text[i]
		var next byte
		if i+1 < n {
			next = text[i+1]
		}
		switch state {
		case stateCode:
			if c == '/' && next == '/' {
				mask[i] = false
				mask[i+1] = false
				i += 2
				state = stateLine
				continue
			}
			if c == '/' && next == '*' {
				mask[i] = false
				mask[i+1] = false
				i += 2
				state = stateBlock
				continue
			}
			if c == '"' {
				state = stateString
				i++
				continue
			}
			if c == '\'' {
				state = stateChar
				i++
				continue
			}
			i++
		case stateLine:
			if c == '\n' {
				state = stateCode
			} else {
				mask[i] = false
			}
			i++
		case stateBlock:
			mask[i] = false
			if c == '*' && next == '/' {
				mask[i+1] = false
				i += 2
				state = stateCode
				continue
			}
			i++
		case stateString:
			if c == '\\' {
				mask[i] = false
				if i+1 < n {
					mask[i+1] = false
				}
				i += 2
				continue
			}
			if c == '"' {
				state = stateCode
			}
			i++
		case stateChar:
			if c == '\\' {
				mask[i] = false
				if i+1 < n {
					mask[i+1] = false
				}
				i += 2
				continue
			}
			if c == '\'' {
				state = stateCode
			}
			i++
		}
	}
	return mask
}

// productFiles lists every .cs file under the given roots, skipping build
// output.
//
// The order is python's `os.walk` order: the files of a directory, sorted,
// then the sub-directories, sorted, one after the other. It is NOT
// filepath.WalkDir's order, which interleaves the files and the
// directories of one level, and the report prints the files in this order.
func productFiles(repo string, roots []string) []string {
	var out []string
	for _, root := range roots {
		base := filepath.Join(repo, filepath.FromSlash(root))
		info, err := os.Stat(base)
		if err != nil || !info.IsDir() {
			continue
		}
		out = append(out, walkDir(base)...)
	}
	return out
}

func walkDir(dir string) []string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var files []string
	var dirs []string
	for _, e := range entries {
		if e.IsDir() {
			if !skipDirs[e.Name()] {
				dirs = append(dirs, e.Name())
			}
			continue
		}
		if strings.HasSuffix(e.Name(), ".cs") {
			files = append(files, e.Name())
		}
	}
	sort.Strings(files)
	sort.Strings(dirs)
	var out []string
	for _, f := range files {
		out = append(out, filepath.Join(dir, f))
	}
	for _, d := range dirs {
		out = append(out, walkDir(filepath.Join(dir, d))...)
	}
	return out
}

// relativePath is the repo-relative, slash-separated path of a file.
func relativePath(repo string, path string) string {
	rel, err := filepath.Rel(repo, path)
	if err != nil {
		return filepath.ToSlash(path)
	}
	return filepath.ToSlash(rel)
}

// projectDir is the directory holding the .csproj that compiles this file.
func projectDir(path string) string {
	dir := filepath.Dir(path)
	for {
		entries, err := os.ReadDir(dir)
		if err == nil {
			for _, e := range entries {
				if !e.IsDir() && strings.HasSuffix(e.Name(), ".csproj") {
					return dir
				}
			}
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return filepath.Dir(path)
		}
		dir = parent
	}
}

// hasGlobalUsing whether the compiling project declares the using globally.
func hasGlobalUsing(path string, usingLine string) bool {
	candidate := filepath.Join(projectDir(path), "GlobalUsings.cs")
	data, err := os.ReadFile(candidate)
	if err != nil {
		return false
	}
	return strings.Contains(string(data), usingLine)
}

var namespaceRe = regexp.MustCompile(`(?m)^namespace\s+([\w.]+)\s*;`)

// namespaceOf the file's namespace, or "".
func namespaceOf(text string) string {
	m := namespaceRe.FindStringSubmatch(text)
	if m == nil {
		return ""
	}
	return m[1]
}

// needsUsing whether this file has to import the catalog's namespace.
func needsUsing(path string, text string) bool {
	if namespaceOf(text) == catalogNS {
		return false
	}
	if strings.Contains(text, catalogUsing) {
		return false
	}
	return !hasGlobalUsing(path, catalogUsing)
}

// usingKey is the sort key of a using directive: System first, then
// alphabetical.
//
// The repository's blocks are laid out that way (the SDK's default), and an
// inserted directive that lands in the wrong group is a diff a reviewer has
// to fix by hand.
func usingKey(line string) (int, string) {
	namespace := strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(line, "using "), ";"))
	if namespace == "System" || strings.HasPrefix(namespace, "System.") {
		return 0, namespace
	}
	return 1, namespace
}

// insertUsing adds the catalog's using to a file's using block, in sorted
// position.
func insertUsing(text string) string {
	lines := strings.Split(text, "\n")
	var plain []int
	for i, line := range lines {
		if strings.HasPrefix(line, "using ") && !strings.Contains(line, " = ") {
			plain = append(plain, i)
		}
	}
	if len(plain) == 0 {
		// No using block: put one above the namespace declaration.
		for i, line := range lines {
			if strings.HasPrefix(line, "namespace ") {
				head := append([]string{catalogUsing, ""}, lines[i:]...)
				return strings.Join(head, "\n")
			}
		}
		return catalogUsing + "\n" + text
	}

	keyGroup, keyNamespace := usingKey(catalogUsing)
	insertAt := plain[len(plain)-1] + 1
	for _, i := range plain {
		group, namespace := usingKey(lines[i])
		if group > keyGroup || (group == keyGroup && namespace > keyNamespace) {
			insertAt = i
			break
		}
	}
	out := append([]string{}, lines[:insertAt]...)
	out = append(out, catalogUsing)
	out = append(out, lines[insertAt:]...)
	return strings.Join(out, "\n")
}

// classify says why a literal is left alone.
func classify(line string, stripped string, previous string) string {
	if strings.HasPrefix(stripped, "//") || strings.HasPrefix(stripped, "///") ||
		strings.HasPrefix(stripped, "*") {
		return "documentation"
	}
	if strings.Contains(line, "[ToolInfo(") ||
		strings.HasSuffix(strings.TrimSpace(previous), "[ToolInfo(") {
		return "C# requires a compile-time constant (attribute argument)"
	}
	if caseLabelRe.MatchString(line) {
		return "C# requires a compile-time constant (case label)"
	}
	if jsonPropertyRe.MatchString(line) && strings.Contains(line, "properties") {
		return "JSON schema property"
	}
	if jsonKeyRe.MatchString(line) {
		return "JSON argument key"
	}
	if pluralRe.MatchString(line) {
		return "display label, not a tool name"
	}
	return "not a tool name in this position"
}

var (
	caseLabelRe    = regexp.MustCompile(`\bcase\s+"`)
	jsonPropertyRe = regexp.MustCompile(`"[a-z][a-z0-9_]*"\s*:`)
	jsonKeyRe      = regexp.MustCompile(`GetProperty\(|GetString\(|RequiredString\(|RequiredNonBlankString\(`)
	pluralRe       = regexp.MustCompile(`\bPlural\(`)
)

// auditRow is one tool-name literal that stays in the tree.
type auditRow struct {
	rel    string
	number int
	name   string
	reason string
	text   string
}

// audit lists every tool-name literal still in the tree, with the reason it
// stays.
func audit(repo string, cat *catalog) []auditRow {
	var rows []auditRow
	for _, path := range productFiles(repo, auditRoots) {
		rel := relativePath(repo, path)
		if rel == catalogRel {
			continue
		}
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		previous := ""
		for number, line := range strings.Split(string(data), "\n") {
			for _, name := range cat.sortedNames() {
				quoted := "\"" + name + "\""
				if !strings.Contains(line, quoted) {
					continue
				}
				stripped := strings.TrimSpace(line)
				reason := classify(line, stripped, previous)
				if strings.HasPrefix(rel, "samples/") {
					reason = "samples/ is out of the rewrite scope: a CS-source plugin is " +
						"compiled against a fixed set of injected namespaces"
				}
				for i := 0; i < strings.Count(line, quoted); i++ {
					rows = append(rows, auditRow{rel, number + 1, name, reason, stripped})
				}
			}
			previous = line
		}
	}
	return rows
}

// pendingFile is one file the rewrite would change.
type pendingFile struct {
	rel     string
	path    string
	applied []hit
	text    string
	newText string
}

// Run is the tool entry point.
func Run(args []string) int {
	repo := ""
	apply := false
	auditMode := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--repo":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--repo needs a path")
				return gate.Broken
			}
			i++
			repo = args[i]
		case "--check":
			// The default: report the pending rewrites and exit 1 if there
			// are any.
		case "--apply":
			apply = true
		case "--audit":
			auditMode = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates codemod-tool-names [--repo PATH] [--check] [--apply] [--audit]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "codemod-tool-names: error: unrecognized argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if repo == "" {
		root, err := gate.BinaryRoot()
		if err != nil {
			root, err = gate.RepoRoot()
			if err != nil {
				fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
				return gate.Broken
			}
		}
		repo = root
	}
	abs, err := filepath.Abs(repo)
	if err == nil {
		repo = abs
	}

	cat, err := readCatalog(repo)
	if err != nil {
		// The python original raised SystemExit with the message, which
		// prints it on stderr and exits 1.
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}

	if auditMode {
		rows := audit(repo, cat)
		byReason := map[string][]auditRow{}
		var order []string
		for _, row := range rows {
			if _, seen := byReason[row.reason]; !seen {
				order = append(order, row.reason)
			}
			byReason[row.reason] = append(byReason[row.reason], row)
		}
		fmt.Printf("tool-name literals left in %s: %d\n\n", strings.Join(auditRoots, ", "), len(rows))
		sorted := append([]string{}, order...)
		sort.Strings(sorted)
		for _, reason := range sorted {
			entries := byReason[reason]
			fmt.Printf("%s (%d)\n", reason, len(entries))
			for _, e := range entries {
				text := e.text
				if len(text) > 90 {
					text = text[:90]
				}
				fmt.Printf("  %s:%d  \"%s\"  %s\n", e.rel, e.number, e.name, text)
			}
			fmt.Println()
		}
		return gate.Green
	}

	var pending []pendingFile
	for _, path := range productFiles(repo, rewriteRoots) {
		rel := relativePath(repo, path)
		if rel == catalogRel {
			continue
		}
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		text := string(data)
		newText, applied := rewrite(text, rel, cat)
		if len(applied) == 0 {
			continue
		}
		if needsUsing(path, text) && strings.Contains(newText, catalogType+".") {
			newText = insertUsing(newText)
		}
		pending = append(pending, pendingFile{rel, path, applied, text, newText})
	}

	total := 0
	for _, p := range pending {
		total += len(p.applied)
	}
	if len(pending) == 0 {
		fmt.Println("nothing to rewrite: every tool-name literal that the rules cover is already a catalog member")
		return gate.Green
	}

	for _, p := range pending {
		seen := map[string]bool{}
		var names []string
		for _, h := range p.applied {
			if seen[h.name] {
				continue
			}
			seen[h.name] = true
			names = append(names, h.name)
		}
		sort.Strings(names)
		fmt.Printf("%s: %d literal(s) -> %s\n", p.rel, len(p.applied), strings.Join(names, ", "))
	}

	fmt.Printf("\n%d file(s), %d literal(s)\n", len(pending), total)

	if apply {
		for _, p := range pending {
			if err := os.WriteFile(p.path, []byte(p.newText), 0o644); err != nil {
				fmt.Fprintln(os.Stderr, "cannot write", p.path+":", err)
				return gate.Broken
			}
		}
		fmt.Println("applied")
		return gate.Green
	}

	fmt.Println("re-run with --apply to write these changes")
	return gate.Red
}
