// Package genplugininventory is the compiled port of
// tools/gen-plugin-inventory.py.
//
// It reads the plugin composition out of the C# sources and the sample
// plugins, and writes the machine-readable snapshot the product writes at
// runtime — in the same schema, so the two producers can be compared.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package genplugininventory

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

// The schema version this generator writes. It is the product's constant; the
// two must agree, and the check below is what says so.
const schemaVersion = 1

// declaredPhase is the phase of a plugin that is in the repository and
// nowhere else. The live phases are `pending`, `active` and `failed`, and
// they belong to the runtime document.
const declaredPhase = "declared"

// The trees the plugin catalogue is read from. `contrib/` is deliberately NOT
// here: it is not compiled by CI and the owner marked it unmaintained, so a
// plugin that only exists there is not a plugin of this product.
var pluginTrees = []string{"samples/plugins-cs", "samples/plugins"}

// The axis registry row is
//
//	new("IToolPlugin", nameof(IPluginLoadHost.RegisterTool)),
var axisRowRe = regexp.MustCompile(
	`new\(\s*"(?P<marker>[A-Za-z_]\w*)"\s*,\s*nameof\(\s*` +
		`(?:[A-Za-z_]\w*\.)?(?P<door>[A-Za-z_]\w*)\s*\)\s*\)`)

// routeConstRe is a public const string in the route registry.
var routeConstRe = regexp.MustCompile(
	`public\s+const\s+string\s+(?P<name>[A-Za-z_]\w*)\s*=\s*"(?P<value>[^"]*)"\s*;`)

// routeRowRe is the route registry row, with the reason a string literal
// that is sometimes split over several lines and concatenated with `+`.
var routeRowRe = regexp.MustCompile(
	`(?s)new\(\s*(?P<route>[A-Za-z_]\w*)\s*,\s*nameof\(\s*` +
		`(?:[A-Za-z_]\w*\s*\.\s*)*(?P<door>[A-Za-z_]\w*)\s*\)\s*,\s*` +
		`PluginAxisCarriage\s*\.\s*(?P<carriage>[A-Za-z_]\w*)\s*,\s*` +
		`(?P<reason>(?:"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+)`)

var stringLiteralRe = regexp.MustCompile(`"((?:[^"\\]|\\.)*)"`)

// classRe is a type declaration.
var classRe = regexp.MustCompile(
	`(?s)\bclass\s+(?P<name>[A-Za-z_]\w*)\s*(?::\s*(?P<bases>[^{]*?))?\s*\{`)

// stringNameRe is the plugin's name.
var stringNameRe = regexp.MustCompile(`public\s+string\s+Name\s*=>\s*"(?P<value>[^"]*)"\s*;`)

// versionRe is the plugin's version.
var versionRe = regexp.MustCompile(
	`public\s+Version\s+Version\s*=>\s*(?:new\s*\(\s*(?P<a>\d+)\s*,\s*(?P<b>\d+)\s*` +
		`(?:,\s*(?P<c>\d+)\s*)?\)|new\s+Version\s*\(\s*(?P<a2>\d+)\s*,\s*(?P<b2>\d+)\s*` +
		`(?:,\s*(?P<c2>\d+)\s*)?\))`)

// dependenciesRe is the plugin's dependencies.
var dependenciesRe = regexp.MustCompile(
	`(?s)public\s+IReadOnlyList<string>\s+Dependencies\s*=>\s*\[(?P<body>[^\]]*)\]`)

// writeMemberRe is a member name the C# writer writes:
// `writer.WriteString("name", ...)`, `writer.WriteStartArray("axes")`, or the
// helper `WriteStringArray(writer, "axes", ...)`.
var writeMemberRe = regexp.MustCompile(
	`\bWrite[A-Za-z]*\s*\(\s*(?:writer\s*,\s*)?"(?P<name>[A-Za-z_][A-Za-z0-9_]*)"`)

// writeArrayRe is a call that opens an array in the product's writer.
var writeArrayRe = regexp.MustCompile(`\bWrite[A-Za-z]*\s*\(\s*"(?P<name>[A-Za-z_][A-Za-z0-9_]*)"`)

// axis is one axis, as the product registry declares it.
type axis struct {
	Marker string
	Door   string
}

// routeRow is one axis on one route, as the route registry declares it.
type routeRow struct {
	Route    string
	Door     string
	Marker   string
	Carriage string
	Reason   string
}

// treePlugin is one plugin the repository ships.
type treePlugin struct {
	Name         string
	Version      string
	Source       string
	Axes         []string
	Dependencies []string
	Manifest     bool
}

// The document, in the order the python original wrote it.
type axisDoc struct {
	Marker string `json:"marker"`
	Door   string `json:"door"`
}

type routeDoc struct {
	Route    string `json:"route"`
	Door     string `json:"door"`
	Marker   string `json:"marker"`
	Carriage string `json:"carriage"`
	Reason   string `json:"reason"`
}

type pluginDoc struct {
	Name                    string   `json:"name"`
	Version                 string   `json:"version"`
	Source                  string   `json:"source"`
	Phase                   string   `json:"phase"`
	Axes                    []string `json:"axes"`
	Dependencies            []string `json:"dependencies"`
	UnsatisfiedDependencies []string `json:"unsatisfiedDependencies"`
	Manifest                bool     `json:"manifest"`
	Registrations           []string `json:"registrations"`
	FailureReason           *string  `json:"failureReason"`
}

type document struct {
	SchemaVersion int         `json:"schemaVersion"`
	Route         *string     `json:"route"`
	Axes          []axisDoc   `json:"axes"`
	Routes        []routeDoc  `json:"routes"`
	Plugins       []pluginDoc `json:"plugins"`
}

// Run is the generator's entry point.
func Run(args []string) int {
	repo := ""
	out := ""
	write := false
	check := false
	reportMode := false
	selfTest := false
	minAxes := 0
	minRoutes := 0
	minPlugins := 0

	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--repo":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --repo needs a path")
				return gate.Broken
			}
			repo = args[index]
		case "--out":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --out needs a path")
				return gate.Broken
			}
			out = args[index]
		case "--write":
			write = true
		case "--check":
			check = true
		case "--report":
			reportMode = true
		case "--self-test":
			selfTest = true
		case "--min-axes":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --min-axes needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --min-axes needs a number: %s\n", args[index])
				return gate.Broken
			}
			minAxes = value
		case "--min-routes":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --min-routes needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --min-routes needs a number: %s\n", args[index])
				return gate.Broken
			}
			minRoutes = value
		case "--min-plugins":
			index++
			if index >= len(args) {
				fmt.Fprintln(os.Stderr, "unknown argument: --min-plugins needs a number")
				return gate.Broken
			}
			value, ok := pycompat.Atoi(args[index])
			if !ok {
				fmt.Fprintf(os.Stderr, "unknown argument: --min-plugins needs a number: %s\n", args[index])
				return gate.Broken
			}
			minPlugins = value
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	root, err := gate.BinaryRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	if repo == "" {
		repo = root
	}
	if out == "" {
		out = filepath.Join(root, "docs", "PLUGIN_INVENTORY.json")
	}
	repo, err = filepath.Abs(repo)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	out, err = filepath.Abs(out)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}

	if selfTest {
		return selfTestRun(repo)
	}
	if write {
		return writeOutput(repo, out)
	}
	if check {
		return checkOutput(repo, out, minAxes, minRoutes, minPlugins, func(line string) { fmt.Print(line) })
	}
	if reportMode {
		return report(repo)
	}
	fmt.Println("usage: harbor-gates gen-plugin-inventory [--write] [--check] [--report] [--self-test]")
	return gate.Broken
}

// readAxes is the axes the product registry declares, in the order it
// declares them.
func readAxes(repo string) []axis {
	path := filepath.Join(repo, "src", "Harbor.Plugins.Abstractions", "PluginAxisRegistry.cs")
	text, ok := readText(path)
	if !ok {
		return nil
	}
	var out []axis
	for _, match := range axisRowRe.FindAllStringSubmatch(text, -1) {
		out = append(out, axis{
			Marker: match[axisRowRe.SubexpIndex("marker")],
			Door:   match[axisRowRe.SubexpIndex("door")],
		})
	}
	return out
}

// readRoutes is the per-route verdicts the product declares, with their
// reasons.
func readRoutes(repo string, markerOfDoor map[string]string) []routeRow {
	path := filepath.Join(repo, "src", "Harbor.Plugins.Hosting", "PluginRouteAxisRegistry.cs")
	text, ok := readText(path)
	if !ok {
		return nil
	}
	constants := map[string]string{}
	for _, match := range routeConstRe.FindAllStringSubmatch(text, -1) {
		constants[match[routeConstRe.SubexpIndex("name")]] = match[routeConstRe.SubexpIndex("value")]
	}

	var rows []routeRow
	for _, match := range routeRowRe.FindAllStringSubmatch(text, -1) {
		route := match[routeRowRe.SubexpIndex("route")]
		if value, ok := constants[route]; ok {
			route = value
		}
		door := match[routeRowRe.SubexpIndex("door")]
		marker, ok := markerOfDoor[door]
		if !ok {
			marker = door
		}
		var reason strings.Builder
		for _, literal := range stringLiteralRe.FindAllStringSubmatch(
			match[routeRowRe.SubexpIndex("reason")], -1) {
			reason.WriteString(literal[1])
		}
		rows = append(rows, routeRow{
			Route:    route,
			Door:     door,
			Marker:   marker,
			Carriage: match[routeRowRe.SubexpIndex("carriage")],
			Reason:   reason.String(),
		})
	}
	return rows
}

// readTreePlugins is the plugins the repository ships, with the axes each one
// carries.
func readTreePlugins(repo string, axisOrder []string) []treePlugin {
	known := map[string]bool{}
	for _, marker := range axisOrder {
		known[marker] = true
	}
	var found []treePlugin

	for _, tree := range pluginTrees {
		base := filepath.Join(repo, tree)
		if info, err := os.Stat(base); err != nil || !info.IsDir() {
			continue
		}
		for _, path := range sortedFiles(base, ".cs") {
			relative, err := filepath.Rel(repo, path)
			if err != nil {
				continue
			}
			rel := filepath.ToSlash(relative)
			raw, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			code := blankComments(string(raw))
			for _, match := range classRe.FindAllStringSubmatchIndex(code, -1) {
				bases := groupOf(code, match, classRe, "bases")
				var markers []string
				for _, base := range splitBases(bases) {
					if known[base] {
						markers = append(markers, base)
					}
				}
				if len(markers) == 0 {
					continue
				}
				body, ok := classBody(code, match[1]-1)
				if !ok {
					continue
				}
				manifestPath := filepath.Join(filepath.Dir(path),
					strings.TrimSuffix(filepath.Base(path), ".cs")+".harbor-plugin.json")
				_, manifestErr := os.Stat(manifestPath)
				plugin, ok := readPlugin(body, rel, markers, axisOrder, manifestErr == nil)
				if !ok {
					continue
				}
				found = append(found, plugin)
			}
		}
	}

	sort.SliceStable(found, func(i, j int) bool { return found[i].Source < found[j].Source })
	return found
}

// splitBases is the names in a base list, without the generic arguments.
func splitBases(body string) []string {
	var out []string
	for _, part := range strings.Split(body, ",") {
		name := strings.TrimSpace(part)
		if index := strings.Index(name, "<"); index >= 0 {
			name = name[:index]
		}
		name = strings.TrimSpace(name)
		if index := strings.LastIndex(name, "."); index >= 0 {
			name = name[index+1:]
		}
		name = strings.TrimSpace(name)
		if name != "" {
			out = append(out, name)
		}
	}
	return out
}

// readPlugin is one plugin out of a class body, or nothing when it does not
// declare one.
func readPlugin(body, rel string, markers, axisOrder []string, manifest bool) (treePlugin, bool) {
	nameMatch := stringNameRe.FindStringSubmatch(body)
	if nameMatch == nil {
		return treePlugin{}, false
	}
	versionMatch := versionRe.FindStringSubmatch(body)
	if versionMatch == nil {
		return treePlugin{}, false
	}
	version := formatVersion(versionMatch, versionRe)

	// The axes are reported in the order the registry declares them, so the
	// tree's answer and the runtime's answer are the same list.
	order := map[string]int{}
	for index, marker := range axisOrder {
		order[marker] = index
	}
	axes := append([]string{}, markers...)
	sort.SliceStable(axes, func(i, j int) bool {
		left, leftOk := order[axes[i]]
		right, rightOk := order[axes[j]]
		if leftOk != rightOk {
			return leftOk
		}
		if !leftOk {
			return axes[i] < axes[j]
		}
		return left < right
	})

	dependencies, err := readDependencies(body, rel)
	if err != nil {
		return treePlugin{}, false
	}

	return treePlugin{
		Name:         nameMatch[stringNameRe.SubexpIndex("value")],
		Version:      version,
		Source:       rel,
		Axes:         axes,
		Dependencies: dependencies,
		Manifest:     manifest,
	}, true
}

// formatVersion is the version a plugin declares, the way the python
// original formats it.
func formatVersion(match []string, re *regexp.Regexp) string {
	pick := func(name string) string {
		value := match[re.SubexpIndex(name)]
		if value == "" {
			return ""
		}
		return value
	}
	major := pick("a")
	if major == "" {
		major = pick("a2")
	}
	minor := pick("b")
	if minor == "" {
		minor = pick("b2")
	}
	build := pick("c")
	if build == "" {
		build = pick("c2")
	}
	if build == "" {
		return major + "." + minor
	}
	return major + "." + minor + "." + build
}

// readDependencies is the dependencies a plugin declares, or a loud failure
// when it cannot be read.
func readDependencies(body, rel string) ([]string, error) {
	match := dependenciesRe.FindStringSubmatch(body)
	if match == nil {
		if strings.Contains(body, "Dependencies") {
			return nil, fmt.Errorf("%s: the plugin declares 'Dependencies' in a form this generator "+
				"cannot read. The generator refuses to claim that such a plugin needs nothing - "+
				"teach it the form, or the document is a lie.", rel)
		}
		return nil, nil
	}
	var out []string
	for _, literal := range stringLiteralRe.FindAllStringSubmatch(match[dependenciesRe.SubexpIndex("body")], -1) {
		out = append(out, literal[1])
	}
	return out, nil
}

// readProductMembers is the member names the product's writer writes.
func readProductMembers(repo string) map[string]bool {
	path := filepath.Join(repo, "src", "Harbor.Plugins.Hosting", "PluginInventory.cs")
	text, ok := readText(path)
	if !ok {
		return map[string]bool{}
	}
	blanked := blankComments(text)
	out := map[string]bool{}
	for _, match := range writeMemberRe.FindAllStringSubmatch(blanked, -1) {
		out[match[writeMemberRe.SubexpIndex("name")]] = true
	}
	return out
}

// readArrayMembers is the member names the product's writer writes INSIDE one
// array.
//
// An array the tree document leaves empty carries no members of its own —
// there is nothing in it to name. The members of its ELEMENTS are still part
// of the schema, and they are read here so the schema check can tell "the
// document has no registrations" apart from "the product renamed a member".
func readArrayMembers(repo, array string) map[string]bool {
	path := filepath.Join(repo, "src", "Harbor.Plugins.Hosting", "PluginInventory.cs")
	text, ok := readText(path)
	if !ok {
		return map[string]bool{}
	}
	blanked := blankComments(text)

	start := strings.Index(blanked, `WriteStartArray("`+array+`")`)
	if start < 0 {
		return map[string]bool{}
	}
	end := strings.Index(blanked[start:], "WriteEndArray()")
	if end < 0 {
		return map[string]bool{}
	}
	region := blanked[start : start+end]
	out := map[string]bool{}
	for _, match := range writeMemberRe.FindAllStringSubmatch(region, -1) {
		out[match[writeMemberRe.SubexpIndex("name")]] = true
	}
	return out
}

// buildDocument is the snapshot, as the data the JSON writer renders.
func buildDocument(axes []axis, routes []routeRow, plugins []treePlugin) *document {
	doc := &document{
		SchemaVersion: schemaVersion,
		Axes:          []axisDoc{},
		Routes:        []routeDoc{},
		Plugins:       []pluginDoc{},
	}
	for _, item := range axes {
		doc.Axes = append(doc.Axes, axisDoc{Marker: item.Marker, Door: item.Door})
	}
	for _, item := range routes {
		doc.Routes = append(doc.Routes, routeDoc{
			Route:    item.Route,
			Door:     item.Door,
			Marker:   item.Marker,
			Carriage: item.Carriage,
			Reason:   item.Reason,
		})
	}
	for _, item := range plugins {
		doc.Plugins = append(doc.Plugins, pluginDoc{
			Name:                    item.Name,
			Version:                 item.Version,
			Source:                  item.Source,
			Phase:                   declaredPhase,
			Axes:                    nonNilStrings(item.Axes),
			Dependencies:            nonNilStrings(item.Dependencies),
			UnsatisfiedDependencies: []string{},
			Manifest:                item.Manifest,
			Registrations:           []string{},
		})
	}
	return doc
}

// renderDocument is the document, exactly as it is committed.
//
// No commit stamp: a stamp would make the file differ from its own
// regeneration on every commit, and the check would then fail on the commit
// that regenerated it. The provenance is the file's own history.
func renderDocument(doc *document) string {
	data, err := pycompat.JSONIndent(doc)
	if err != nil {
		return ""
	}
	return string(data) + "\n"
}

// membersOf is every member name the document carries, at any level.
func membersOf(doc *document) map[string]bool {
	names := map[string]bool{}
	names["schemaVersion"] = true
	names["route"] = true
	names["axes"] = true
	names["routes"] = true
	names["plugins"] = true
	for range doc.Axes {
		names["marker"] = true
		names["door"] = true
	}
	for range doc.Routes {
		names["route"] = true
		names["door"] = true
		names["marker"] = true
		names["carriage"] = true
		names["reason"] = true
	}
	for range doc.Plugins {
		names["name"] = true
		names["version"] = true
		names["source"] = true
		names["phase"] = true
		names["axes"] = true
		names["dependencies"] = true
		names["unsatisfiedDependencies"] = true
		names["manifest"] = true
		names["registrations"] = true
		names["failureReason"] = true
	}
	return names
}

// emptyArrays is the names of the arrays the document leaves empty.
func emptyArrays(doc *document) map[string]bool {
	names := map[string]bool{}
	if len(doc.Axes) == 0 {
		names["axes"] = true
	}
	if len(doc.Routes) == 0 {
		names["routes"] = true
	}
	if len(doc.Plugins) == 0 {
		names["plugins"] = true
	}
	for _, item := range doc.Plugins {
		if len(item.Axes) == 0 {
			names["axes"] = true
		}
		if len(item.Dependencies) == 0 {
			names["dependencies"] = true
		}
		if len(item.UnsatisfiedDependencies) == 0 {
			names["unsatisfiedDependencies"] = true
		}
		if len(item.Registrations) == 0 {
			names["registrations"] = true
		}
	}
	return names
}

// collect is the document the tree declares, and the member names the
// product writes.
func collect(repo string) (*document, map[string]bool) {
	axes := readAxes(repo)
	markerOfDoor := map[string]string{}
	markers := make([]string, 0, len(axes))
	for _, item := range axes {
		markerOfDoor[item.Door] = item.Marker
		markers = append(markers, item.Marker)
	}
	routes := readRoutes(repo, markerOfDoor)
	plugins := readTreePlugins(repo, markers)
	return buildDocument(axes, routes, plugins), readProductMembers(repo)
}

// writeOutput regenerates the document.
func writeOutput(repo, out string) int {
	doc, _ := collect(repo)
	os.MkdirAll(filepath.Dir(out), 0o755)
	if err := os.WriteFile(out, []byte(renderDocument(doc)), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	shown := out
	if relative, err := filepath.Rel(repo, out); err == nil && !strings.HasPrefix(relative, "..") {
		shown = relative
	}
	fmt.Printf("wrote %s\n", shown)
	return gate.Green
}

// checkOutput fails when the committed document no longer matches the tree.
func checkOutput(repo, out string, minAxes, minRoutes, minPlugins int, emit func(string)) int {
	if info, err := os.Stat(out); err != nil || info.IsDir() {
		emit(fmt.Sprintf("FAIL: %s does not exist; run --write\n", out))
		return gate.Red
	}

	doc, productMembers := collect(repo)
	axes := doc.Axes
	routes := doc.Routes
	plugins := doc.Plugins

	if len(axes) < minAxes {
		emit(fmt.Sprintf("FAIL: only %d axes were read, floor is %d. A scanner that stopped "+
			"matching the registry would produce a document that agrees with itself and says nothing.\n",
			len(axes), minAxes))
		return gate.Red
	}
	if len(routes) < minRoutes {
		emit(fmt.Sprintf("FAIL: only %d route verdicts were read, floor is %d. A scanner that stopped "+
			"matching the route registry would produce a document that agrees with itself and says nothing.\n",
			len(routes), minRoutes))
		return gate.Red
	}
	if len(plugins) < minPlugins {
		emit(fmt.Sprintf("FAIL: only %d tree plugins were read, floor is %d. A scan that stopped "+
			"covering the sample tree would produce a document that agrees with itself and says nothing.\n",
			len(plugins), minPlugins))
		return gate.Red
	}

	// The product owns the schema. This generator mirrors it, and a member the
	// product renamed is a member the consumers of this document would not
	// find any more.
	if len(productMembers) > 0 {
		documentMembers := membersOf(doc)
		var missing []string
		for name := range documentMembers {
			if !productMembers[name] {
				missing = append(missing, name)
			}
		}
		sort.Strings(missing)
		if len(missing) > 0 {
			emit(fmt.Sprintf("FAIL: the document carries member(s) the product's writer does not write: %s. "+
				"The product owns the format; update the writer, then regenerate.\n",
				strings.Join(missing, ", ")))
			return gate.Red
		}

		// A member the product writes INSIDE an array the document leaves
		// empty is not a member the document is missing: there is nothing in
		// the array to name. The tree declares plugins; it does not run them.
		exempt := map[string]bool{}
		for array := range emptyArrays(doc) {
			for name := range readArrayMembers(repo, array) {
				exempt[name] = true
			}
		}

		var extra []string
		for name := range productMembers {
			if documentMembers[name] || exempt[name] {
				continue
			}
			extra = append(extra, name)
		}
		sort.Strings(extra)
		if len(extra) > 0 {
			emit(fmt.Sprintf("FAIL: the product's writer writes member(s) this document does not carry: %s. "+
				"The two producers must agree on the schema; update this generator, then regenerate.\n",
				strings.Join(extra, ", ")))
			return gate.Red
		}
	}

	expected := renderDocument(doc)
	actual, err := os.ReadFile(out)
	if err != nil {
		emit(fmt.Sprintf("FAIL: %s is stale: the tree changed since it was generated.\n", filepath.Base(out)))
		return gate.Red
	}
	if expected == string(actual) {
		emit(fmt.Sprintf("OK: %s matches the tree (%d axes, %d route verdicts, %d plugins)\n",
			filepath.Base(out), len(axes), len(routes), len(plugins)))
		return gate.Green
	}

	emit(fmt.Sprintf("FAIL: %s is stale: the tree changed since it was generated.\n", filepath.Base(out)))
	expLines := pycompat.SplitLines(expected)
	actLines := pycompat.SplitLines(string(actual))
	differed := false
	for index := 0; index < len(expLines) && index < len(actLines); index++ {
		if expLines[index] != actLines[index] {
			emit(fmt.Sprintf("  first difference at line %d:\n", index+1))
			emit(fmt.Sprintf("    committed: %s\n", actLines[index]))
			emit(fmt.Sprintf("    generated: %s\n", expLines[index]))
			differed = true
			break
		}
	}
	if !differed {
		emit(fmt.Sprintf("  line count differs: committed %d, generated %d\n",
			len(actLines), len(expLines)))
	}
	emit("Run ./tools/gen-plugin-inventory.py --write and commit the result.\n")
	return gate.Red
}

// checkOutputCaptured is checkOutput with its output collected instead of
// printed.
func checkOutputCaptured(repo, out string, minAxes, minRoutes, minPlugins int) (int, string) {
	var lines []string
	code := checkOutput(repo, out, minAxes, minRoutes, minPlugins, func(line string) {
		lines = append(lines, line)
	})
	return code, strings.Join(lines, "\n")
}

// report prints the numbers.
func report(repo string) int {
	doc, productMembers := collect(repo)
	fmt.Printf("axes           : %d\n", len(doc.Axes))
	for _, item := range doc.Axes {
		fmt.Printf("  %-22s %-22s\n", item.Marker, item.Door)
	}
	fmt.Println("")
	fmt.Println("route verdicts (the product's declaration):")
	for _, item := range doc.Routes {
		fmt.Printf("  %-12s %-24s %-11s %s\n", item.Route, item.Door, item.Carriage, item.Marker)
	}
	fmt.Println("")
	fmt.Println("plugins the tree ships:")
	for _, item := range doc.Plugins {
		axes := strings.Join(item.Axes, ", ")
		if axes == "" {
			axes = "(none)"
		}
		fmt.Printf("  %-22s %-10s %-32s %s\n", item.Name, item.Version, axes, item.Source)
	}
	fmt.Println("")
	fmt.Printf("members the product writes : %d\n", len(productMembers))
	fmt.Printf("members the document carries: %d\n", len(membersOf(doc)))
	return gate.Green
}

// groupOf is the text of a named group in a submatch index slice.
func groupOf(text string, match []int, re *regexp.Regexp, name string) string {
	index := re.SubexpIndex(name)
	if index < 0 || 2*index+1 >= len(match) {
		return ""
	}
	start, end := match[2*index], match[2*index+1]
	if start < 0 || end < 0 {
		return ""
	}
	return text[start:end]
}

// readText reads a file, or nothing when it is not there.
func readText(path string) (string, bool) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", false
	}
	return string(data), true
}

// sortedFiles is every file under a directory with the suffix, in sorted
// order.
func sortedFiles(base, suffix string) []string {
	var out []string
	filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			return nil
		}
		if strings.HasSuffix(info.Name(), suffix) {
			out = append(out, path)
		}
		return nil
	})
	sort.Strings(out)
	return out
}

// nonNilStrings is python's `list(...)` of a list that may be empty.
func nonNilStrings(values []string) []string {
	if values == nil {
		return []string{}
	}
	return values
}

// unusedNumber keeps strconv imported for the future.
var _ = strconv.Itoa
