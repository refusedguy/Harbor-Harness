package genseamgraph

// graph.go — the extracted graph: declarations, edges, registrations,
// bypasses, extension axes. A transliteration of tools/gen-seam-graph.py
// sections 4 and 5.

import (
	"bytes"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

const (
	axisRegistry      = "src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs"
	routeAxisRegistry = "src/Harbor.Plugins.Hosting/PluginRouteAxisRegistry.cs"
	loadHostInterface = "src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs"
)

var consumerTrees = []string{"src", "apps", "tests", "samples"}

type edge struct {
	src, dst, kind, file string
	line                 int
}

type registration struct {
	service, impl, lifetime, file string
	line                          int
}

type bypass struct {
	concrete, seam, form, file string
	line                       int
}

type routeAxisRow struct {
	route, hostType, marker, door string
	declared, reason              string
	observed, observedNote        string
}

// agrees is the rule the architecture file grades, in one predicate.
func (r *routeAxisRow) agrees() bool {
	switch r.declared {
	case "Carried":
		return r.observed == "REGISTERS"
	case "Refused", "ClosedSeam":
		return r.observed == "REFUSES"
	}
	return false
}

type axis struct {
	marker, door       string
	markerImplsProduct []*typeDecl
	markerImplsOutside []*typeDecl
	dispatchSites      []site
	doorSites          []site
	declared           []*routeAxisRow
}

type graph struct {
	repo          string
	consumerFiles []*sourceFile
	productFiles  []*sourceFile
	decls         []*typeDecl
	index         map[string][]*typeDecl
	implements    map[string][]*typeDecl
	extends       map[string][]*typeDecl
	edges         []edge
	registrations []registration
	mentions      map[string]map[string][]int
	bypasses      []bypass
	declaredDoors []string
	routeRows     []*routeAxisRow
	otherHosts    []otherHost
}

type otherHost struct {
	decl  *typeDecl
	kinds string
	note  string
}

func newGraph(repo string) *graph {
	g := &graph{
		repo:       repo,
		index:      map[string][]*typeDecl{},
		implements: map[string][]*typeDecl{},
		extends:    map[string][]*typeDecl{},
		mentions:   map[string]map[string][]int{},
	}
	g.consumerFiles = collectSources(repo, consumerTrees)
	for _, f := range g.consumerFiles {
		if strings.HasPrefix(f.rel, "src/") || strings.HasPrefix(f.rel, "apps/") {
			g.productFiles = append(g.productFiles, f)
		}
	}
	phase("collect", time.Now())
	g.decls = extractTypes(g.consumerFiles)
	phase("extract", time.Now())
	for _, d := range g.decls {
		g.index[d.name] = append(g.index[d.name], d)
	}
	g.buildEdges()
	phase("edges", time.Now())
	g.registrations = g.extractRegistrations()
	phase("di", time.Now())
	g.indexMentions()
	phase("mentions", time.Now())
	g.bypasses = g.extractBypasses()
	phase("bypasses", time.Now())
	return g
}

// -- edges ------------------------------------------------------------------

func (g *graph) buildEdges() {
	for _, decl := range g.decls {
		for _, base := range decl.bases {
			target := g.resolve(base, decl.namespace)
			if target == nil {
				continue
			}
			if target.kind == "interface" {
				g.implements[target.name] = append(g.implements[target.name], decl)
				g.edges = append(g.edges, edge{decl.name, target.name, "implements", decl.rel(), decl.line})
			} else {
				g.extends[target.name] = append(g.extends[target.name], decl)
				g.edges = append(g.edges, edge{decl.name, target.name, "extends", decl.rel(), decl.line})
			}
		}
	}
	g.closeImplements()
}

// closeImplements: a class that extends a class which implements an interface
// implements it too.
func (g *graph) closeImplements() {
	changed := true
	for changed {
		changed = false
		for _, impls := range g.implements {
			for _, impl := range impls {
				for _, base := range impl.bases {
					target := g.resolve(base, impl.namespace)
					if target == nil {
						continue
					}
					others, ok := g.implements[target.name]
					if !ok {
						continue
					}
					already := false
					for _, other := range others {
						if other.name == impl.name {
							already = true
							break
						}
					}
					if already {
						continue
					}
					g.implements[target.name] = append(others, impl)
					g.edges = append(g.edges, edge{impl.name, target.name, "extends", impl.rel(), impl.line})
					changed = true
				}
			}
		}
	}
}

// resolve resolves a simple name to a declaration: same namespace first,
// then a unique global match. An ambiguous name resolves to nothing.
func (g *graph) resolve(name, ns string) *typeDecl {
	candidates := g.index[name]
	if len(candidates) == 0 {
		return nil
	}
	if len(candidates) == 1 {
		return candidates[0]
	}
	var sameNs []*typeDecl
	for _, c := range candidates {
		if c.namespace == ns {
			sameNs = append(sameNs, c)
		}
	}
	if len(sameNs) == 1 {
		return sameNs[0]
	}
	return nil
}

// implementers returns the implementers of a seam, sorted by (rel, line).
func (g *graph) implementers(seam string) []*typeDecl {
	out := append([]*typeDecl{}, g.implements[seam]...)
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].rel() != out[j].rel() {
			return out[i].rel() < out[j].rel()
		}
		return out[i].line < out[j].line
	})
	return out
}

// projectOf is the project a repository-relative path belongs to.
func (g *graph) projectOf(rel string) string {
	for _, source := range g.consumerFiles {
		if source.rel == rel {
			return source.project
		}
	}
	return filepath.Base(filepath.Dir(rel))
}

// -- consumers --------------------------------------------------------------

var (
	iPrefixRe     = regexp.MustCompile(`^I[A-Z][A-Za-z0-9_]*$`)
	interfaceName = regexp.MustCompile(`\bI[A-Z][A-Za-z0-9_]*\b`)
)

// indexMentions: one pass over the tree, where each interface NAME is
// mentioned.
func (g *graph) indexMentions() {
	interfaceNames := map[string]bool{}
	for _, d := range g.decls {
		if d.kind == "interface" {
			interfaceNames[d.name] = true
		}
	}
	prefixed := map[string]bool{}
	var named []string
	for name := range interfaceNames {
		if iPrefixRe.MatchString(name) {
			prefixed[name] = true
		} else {
			named = append(named, name)
		}
	}
	sort.Slice(named, func(i, j int) bool {
		if len(named[i]) != len(named[j]) {
			return len(named[i]) > len(named[j])
		}
		return named[i] < named[j]
	})

	_ = prefixed
	_ = named

	// base-list spans, per file: a mention inside a base list is an
	// implementation, not a consumer.
	type span struct{ lo, hi int }
	baseSpans := map[*sourceFile][]span{}
	for _, decl := range g.decls {
		if len(decl.bases) == 0 || decl.baseSpan == nil {
			continue
		}
		baseSpans[decl.file] = append(baseSpans[decl.file], span{decl.baseSpan[0], decl.baseSpan[1]})
	}

	for _, source := range g.consumerFiles {
		code := source.code
		spans := baseSpans[source]
		perFile := map[string][]int{}
		for _, m := range findInterfaceMentions(code, interfaceNames) {
			inside := false
			for _, sp := range spans {
				if sp.lo <= m.start && m.start <= sp.hi {
					inside = true
					break
				}
			}
			if inside {
				continue
			}
			line := source.lineOf(m.start)
			perFile[m.name] = append(perFile[m.name], line)
		}
		if len(perFile) > 0 {
			g.mentions[source.rel] = perFile
		}
	}
}

// consumers returns the files that NAME the seam, per file, with the line of
// each mention.
func (g *graph) consumers(seam string) map[string][]site {
	result := map[string][]site{}
	for rel, perFile := range g.mentions {
		lines := perFile[seam]
		if len(lines) > 0 {
			var out []site
			for _, l := range lines {
				out = append(out, site{rel, l})
			}
			result[rel] = out
		}
	}
	return result
}

// -- registration -----------------------------------------------------------

var diRe = regexp.MustCompile(`\b(TryAdd|Add)(Singleton|Scoped|Transient)(?:Keyed)?<([^<>]*(?:<[^<>]*>[^<>]*)*)>`)

func (g *graph) extractRegistrations() []registration {
	var out []registration
	for _, source := range g.productFiles {
		for _, m := range findDiRegistrations(source.code) {
			argsRaw := m.args
			var args []string
			for _, a := range strings.Split(argsRaw, ",") {
				a = strings.TrimSpace(a)
				if a != "" {
					args = append(args, a)
				}
			}
			if len(args) == 0 {
				continue
			}
			service := strings.TrimSpace(stripGeneric(args[0]))
			impl := ""
			if len(args) > 1 {
				impl = strings.TrimSpace(stripGeneric(args[1]))
			}
			if service == "" {
				continue
			}
			out = append(out, registration{
				service:  service,
				impl:     impl,
				lifetime: m.lifetime,
				file:     source.rel,
				line:     source.lineOf(m.start),
			})
		}
	}
	return out
}

func (g *graph) registrationsFor(service string) []registration {
	var out []registration
	for _, r := range g.registrations {
		if r.service == service {
			out = append(out, r)
		}
	}
	return out
}

// -- bypasses ---------------------------------------------------------------

var (
	constructionRe = regexp.MustCompile(`\bnew\s+([A-Z][A-Za-z0-9_]*)\s*(?:<[^>]*>)?\s*\(`)
	typeThenNameRe = regexp.MustCompile(`\b([A-Z][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)\b`)
)

// extractBypasses finds where the CONCRETE type is used where the seam was
// the contract.
func (g *graph) extractBypasses() []bypass {
	concreteToSeams := map[string]map[string]bool{}
	for seam, impls := range g.implements {
		for _, impl := range impls {
			if impl.kind == "interface" {
				continue
			}
			if concreteToSeams[impl.name] == nil {
				concreteToSeams[impl.name] = map[string]bool{}
			}
			concreteToSeams[impl.name][seam] = true
		}
	}
	if len(concreteToSeams) == 0 {
		return nil
	}

	var out []bypass
	for _, source := range g.productFiles {
		code := source.code
		for _, m := range findConstructions(code) {
			concrete := m.name
			seams, ok := concreteToSeams[concrete]
			if !ok {
				continue
			}
			line := source.lineOf(m.start)
			for _, seam := range sortedKeys(seams) {
				out = append(out, bypass{concrete, seam, "construction", source.rel, line})
			}
		}
		for _, m := range findTypeThenName(code) {
			concrete := m.name
			seams, ok := concreteToSeams[concrete]
			if !ok {
				continue
			}
			line := source.lineOf(m.start)
			for _, seam := range sortedKeys(seams) {
				out = append(out, bypass{concrete, seam, "signature", source.rel, line})
			}
		}
	}
	sort.SliceStable(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if a.file != b.file {
			return a.file < b.file
		}
		if a.line != b.line {
			return a.line < b.line
		}
		if a.concrete != b.concrete {
			return a.concrete < b.concrete
		}
		return a.form < b.form
	})
	seen := map[string]bool{}
	var unique []bypass
	for _, b := range out {
		key := b.concrete + "\x00" + b.seam + "\x00" + b.file + "\x00" + itoa(b.line) + "\x00" + b.form
		if seen[key] {
			continue
		}
		seen[key] = true
		unique = append(unique, b)
	}
	return unique
}

func sortedKeys(m map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// -- axes -------------------------------------------------------------------

var (
	axisRowRe    = regexp.MustCompile(`new\(\s*"([A-Za-z_][A-Za-z0-9_]*)"\s*,\s*nameof\(\s*(?:[A-Za-z_][A-Za-z0-9_]*\.)?([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\)`)
	routeConstRe = regexp.MustCompile(`public\s+const\s+string\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*"([^"]*)"\s*;`)
	routeRowRe   = regexp.MustCompile(`(?s)new\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*nameof\(\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*,\s*PluginAxisCarriage\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*((?:"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+)`)
	stringLitRe  = regexp.MustCompile(`"((?:[^"\\]|\\.)*)"`)
	doorDeclRe   = regexp.MustCompile(`public[ \t]+Result[ \t]+(Register[A-Za-z0-9_]*)[ \t]*\(`)
)

// axes returns the extension axes, and what each route does with them.
func (g *graph) axes() []*axis {
	registryPath := filepath.Join(g.repo, axisRegistry)
	hostPath := filepath.Join(g.repo, loadHostInterface)
	if _, err := os.Stat(registryPath); err != nil {
		g.declaredDoors = nil
		g.routeRows = nil
		g.otherHosts = nil
		return nil
	}
	if _, err := os.Stat(hostPath); err != nil {
		g.declaredDoors = nil
		g.routeRows = nil
		g.otherHosts = nil
		return nil
	}
	registry, err := os.ReadFile(registryPath)
	if err != nil {
		return nil
	}
	hostIface, err := os.ReadFile(hostPath)
	if err != nil {
		return nil
	}

	type pair struct{ marker, door string }
	var pairs []pair
	for _, m := range findAxisRows(registry) {
		pairs = append(pairs, pair{m.marker, m.door})
	}
	declaredDoors := findDoorDecls(hostIface)

	markerOf := map[string]string{}
	for _, p := range pairs {
		markerOf[p.door] = p.marker
	}
	routeRows := g.routeRowsOf(markerOf)

	markerNames := map[string]bool{}
	doorNames := map[string]bool{}
	for _, p := range pairs {
		markerNames[p.marker] = true
		doorNames[p.door] = true
	}
	dispatchSites := g.findSitesByName(findDispatchSites, markerNames)
	doorSites := g.findSitesByName(findDoorCalls, doorNames)

	byDoor := map[string][]*routeAxisRow{}
	for _, row := range routeRows {
		byDoor[row.door] = append(byDoor[row.door], row)
	}

	var out []*axis
	for _, p := range pairs {
		ax := &axis{marker: p.marker, door: p.door}
		for _, impl := range g.implementers(p.marker) {
			if strings.HasPrefix(impl.rel(), "src/") || strings.HasPrefix(impl.rel(), "apps/") {
				ax.markerImplsProduct = append(ax.markerImplsProduct, impl)
			} else {
				ax.markerImplsOutside = append(ax.markerImplsOutside, impl)
			}
		}
		ax.dispatchSites = dispatchSites[p.marker]
		ax.doorSites = doorSites[p.door]
		ax.declared = byDoor[p.door]
		out = append(out, ax)
	}

	g.declaredDoors = declaredDoors
	g.routeRows = routeRows
	g.otherHosts = g.hostsTheRegistryDoesNotName(routeRows)
	return out
}

// routeRowsOf reads the route registry as TEXT.
func (g *graph) routeRowsOf(markerOf map[string]string) []*routeAxisRow {
	path := filepath.Join(g.repo, routeAxisRegistry)
	text, err := os.ReadFile(path)
	if err != nil {
		return nil
	}
	constants := map[string]string{}
	for _, m := range findRouteConsts(text) {
		constants[m.name] = m.value
	}
	hostOfRoute := map[string]string{}
	for name, value := range constants {
		if !strings.HasSuffix(name, "RouteId") {
			continue
		}
		prefix := strings.TrimSuffix(name, "RouteId")
		hostName := prefix + "HostType"
		if v, ok := constants[hostName]; ok {
			hostOfRoute[value] = v
		}
	}
	var rows []*routeAxisRow
	for _, m := range findRouteRows(text) {
		routeConst := m.route
		route := routeConst
		if v, ok := constants[routeConst]; ok {
			route = v
		}
		door := m.door
		declared := m.carriage
		reason := m.reason
		hostType := hostOfRoute[route]
		hostDecl := g.hostByName(hostType)
		observed, note := "", ""
		if hostDecl == nil {
			observed, note = "MISSING", "the product has no such load host"
		} else {
			observed, note = g.gradeDoor(hostDecl, door)
		}
		marker := door
		if m, ok := markerOf[door]; ok {
			marker = m
		}
		rows = append(rows, &routeAxisRow{
			route:        route,
			hostType:     hostType,
			marker:       marker,
			door:         door,
			declared:     declared,
			reason:       reason,
			observed:     observed,
			observedNote: note,
		})
	}
	return rows
}

func (g *graph) hostByName(name string) *typeDecl {
	if name == "" {
		return nil
	}
	for _, decl := range g.implementers("IPluginLoadHost") {
		if decl.name == name && (strings.HasPrefix(decl.rel(), "src/") || strings.HasPrefix(decl.rel(), "apps/")) {
			return decl
		}
	}
	return nil
}

// hostsTheRegistryDoesNotName returns the load hosts the route registry does
// not name, and what they do.
func (g *graph) hostsTheRegistryDoesNotName(routeRows []*routeAxisRow) []otherHost {
	named := map[string]bool{}
	for _, row := range routeRows {
		if row.hostType != "" {
			named[row.hostType] = true
		}
	}
	var out []otherHost
	for _, decl := range g.implementers("IPluginLoadHost") {
		if !strings.HasPrefix(decl.rel(), "src/") && !strings.HasPrefix(decl.rel(), "apps/") {
			continue
		}
		if named[decl.name] {
			continue
		}
		var verdicts [][2]string
		for _, door := range g.declaredDoors {
			verdict, note := g.gradeDoor(decl, door)
			verdicts = append(verdicts, [2]string{verdict, note})
		}
		kindSet := map[string]bool{}
		noteSet := map[string]bool{}
		for _, v := range verdicts {
			kindSet[v[0]] = true
			if v[1] != "" {
				noteSet[v[1]] = true
			}
		}
		kinds := sortedKeys(kindSet)
		notes := sortedKeys(noteSet)
		out = append(out, otherHost{
			decl:  decl,
			kinds: strings.Join(kinds, ", "),
			note:  strings.Join(notes, "; "),
		})
	}
	return out
}

// mutatingPrefixes are the calls MUTATING_CALL_RE lists.
var mutatingPrefixes = []string{
	"Add", "AddOrUpdate", "AddRange", "Append", "GetOrAdd", "Insert", "Put",
	"Push", "Register", "Set", "SetItem", "SetValue", "Store", "TryAdd",
	"TryEnqueue", "Enqueue",
}

// nonEffectCalls are the calls that do not change the state of the host.
var nonEffectCalls = map[string]bool{
	"Log": true, "LogCritical": true, "LogDebug": true, "LogError": true,
	"LogInformation": true, "LogNone": true, "LogTrace": true, "LogWarning": true,
	"Trace": true, "Debug": true, "Info": true, "Information": true, "Warn": true,
	"Warning": true, "Error": true, "Fatal": true,
}

// gradeDoor returns what one host actually does with one door.
func (g *graph) gradeDoor(host *typeDecl, door string) (string, string) {
	body := g.methodBody(host, door)
	if body == nil {
		return "MISSING", "the host does not declare the door"
	}
	if len(findForwardCalls(body)) > 0 {
		return "DELEGATES", "the door forwards the contribution to another host"
	}
	claimsSuccess := len(findResultCalls(body, "Success")) > 0
	claimsFailure := len(findResultCalls(body, "Failure")) > 0
	if claimsSuccess && g.hasEffect(body) {
		return "REGISTERS", "the door answers Result.Success and registers"
	}
	if claimsFailure && !claimsSuccess {
		return "REFUSES", "the door answers Result.Failure"
	}
	if claimsSuccess && claimsFailure {
		return "MIXED", "the door answers Result.Success and Result.Failure"
	}
	if claimsSuccess {
		return "SILENT", "the door answers Result.Success and registers nothing"
	}
	return "UNKNOWN", "the door answers neither Result.Success nor Result.Failure"
}

// hasEffect reports whether a door body changes anything.
func (g *graph) hasEffect(body []byte) bool {
	if hasMemberWrite(body) || hasIndexerWrite(body) {
		return true
	}
	if len(findCallsWithPrefix(body, mutatingPrefixes)) > 0 {
		return true
	}
	if len(findForwardCalls(body)) > 0 {
		return true
	}
	for _, name := range bareCalls(body) {
		if !nonEffectCalls[name] {
			return true
		}
	}
	return false
}

// skipSpace skips the run of spaces and tabs at pos.
func skipSpace(text []byte, pos int) int {
	for pos < len(text) && (text[pos] == ' ' || text[pos] == '\t') {
		pos++
	}
	return pos
}

// takeOperator consumes the optional assignment operator of the write
// patterns, or returns -1 when there is none.
func takeOperator(text []byte, pos int) int {
	if pos+1 < len(text) {
		two := string(text[pos : pos+2])
		if two == "??" || two == "||" || two == "&&" {
			return pos + 2
		}
	}
	if pos < len(text) {
		switch text[pos] {
		case '+', '-', '*', '/', '%', '^', '|':
			return pos + 1
		}
	}
	return -1
}

// hasMemberWrite is `\bthis\s*\.\s*\w+\s*(?:op)?=(?!=)`.
func hasMemberWrite(body []byte) bool {
	n := len(body)
	for i := 0; i+4 <= n; i++ {
		if body[i] != 't' || string(body[i:i+4]) != "this" {
			continue
		}
		if i > 0 && isWordByte(body[i-1]) {
			continue
		}
		j := skipSpace(body, i+4)
		if j >= n || body[j] != '.' {
			continue
		}
		j = skipSpace(body, j+1)
		k := j
		for k < n && isWordByte(body[k]) {
			k++
		}
		if k == j {
			continue
		}
		j = skipSpace(body, k)
		// The operator is optional: try with it, then without.
		candidates := []int{j}
		if op := takeOperator(body, j); op >= 0 {
			candidates = []int{op, j}
		}
		for _, c := range candidates {
			if c < n && body[c] == '=' && !(c+1 < n && body[c+1] == '=') {
				return true
			}
		}
	}
	return false
}

// hasIndexerWrite is `\b\w+\s*\[[^\]]*\]\s*(?:op)?=(?!=)`.
func hasIndexerWrite(body []byte) bool {
	n := len(body)
	for i := 0; i < n; i++ {
		if !isWordByte(body[i]) {
			continue
		}
		if i > 0 && isWordByte(body[i-1]) {
			continue
		}
		j := i
		for j < n && isWordByte(body[j]) {
			j++
		}
		j = skipSpace(body, j)
		if j >= n || body[j] != '[' {
			continue
		}
		k := j + 1
		for k < n && body[k] != ']' {
			k++
		}
		if k >= n {
			continue
		}
		j = skipSpace(body, k+1)
		candidates := []int{j}
		if op := takeOperator(body, j); op >= 0 {
			candidates = []int{op, j}
		}
		for _, c := range candidates {
			if c < n && body[c] == '=' && !(c+1 < n && body[c+1] == '=') {
				return true
			}
		}
	}
	return false
}

// bareCalls is `(?<!new )(?<![.\w])([A-Z]\w*)\s*\(`: the names of the calls
// that are not a member access and not a construction.
func bareCalls(body []byte) []string {
	var out []string
	n := len(body)
	for i := 0; i < n; i++ {
		c := body[i]
		if c < 'A' || c > 'Z' {
			continue
		}
		if i > 0 {
			prev := body[i-1]
			if prev == '.' || isWordByte(prev) {
				continue
			}
			if i >= 4 && string(body[i-4:i]) == "new " {
				continue
			}
		}
		j := i + 1
		for j < n && isWordByte(body[j]) {
			j++
		}
		k := skipSpace(body, j)
		if k >= n || body[k] != '(' {
			continue
		}
		out = append(out, string(body[i:j]))
		i = j - 1
	}
	return out
}

// methodBody returns the body of one method of one type, from the blanked
// source. The search is scoped to the TYPE's own body.
func (g *graph) methodBody(host *typeDecl, door string) []byte {
	for _, source := range g.filesOf(host) {
		code := source.code
		span := typeBodySpan(code, host.name)
		if span == nil {
			continue
		}
		lo, hi := span[0], span[1]
		for _, m := range findNamedCall(code[lo:hi], door) {
			body := bodyAfterSignature(code, lo+m.end, hi)
			if body != nil {
				return body
			}
		}
	}
	return nil
}

// filesOf returns the files a (possibly partial) type is declared in.
func (g *graph) filesOf(decl *typeDecl) []*sourceFile {
	wanted := map[string]bool{}
	for _, s := range decl.sites {
		wanted[s.rel] = true
	}
	if len(wanted) == 0 {
		wanted[decl.rel()] = true
	}
	var out []*sourceFile
	for _, s := range g.consumerFiles {
		if wanted[s.rel] {
			out = append(out, s)
		}
	}
	return out
}

// typeBodySpan is the character span of a type's body, from its declaration.
func typeBodySpan(code []byte, name string) []int {
	end, ok := findTypeDeclBody(code, name)
	if !ok {
		return nil
	}
	i := end
	depth := 0
	n := len(code)
	for i < n {
		ch := code[i]
		switch {
		case ch == '<' || ch == '(' || ch == '[':
			depth++
		case ch == '>' || ch == ')' || ch == ']':
			if depth > 0 {
				depth--
			}
		case depth == 0:
			if ch == '{' {
				close := matchBalanced(code, i, '{', '}')
				if close < 0 {
					return nil
				}
				return []int{i, close + 1}
			}
			if ch == ';' {
				return nil
			}
		}
		i++
	}
	return nil
}

// bodyAfterSignature returns the body of a method, given the `(` of its
// parameter list.
func bodyAfterSignature(code []byte, openParen, limit int) []byte {
	closeParen := matchBalanced(code, openParen, '(', ')')
	if closeParen < 0 || closeParen > limit {
		return nil
	}
	i := closeParen + 1
	for i < len(code) && (code[i] == ' ' || code[i] == '\t' || code[i] == '\r' || code[i] == '\n') {
		i++
	}
	if i >= len(code) {
		return nil
	}
	if code[i] == '{' {
		close := matchBalanced(code, i, '{', '}')
		if close < 0 {
			return nil
		}
		return code[i : close+1]
	}
	if bytes.HasPrefix(code[i:], []byte("=>")) {
		end := bytes.IndexByte(code[i:], ';')
		if end >= 0 {
			return code[i : i+end+1]
		}
		return code[i:]
	}
	return nil
}

// matchBalanced returns the index of the closer that matches the opener at
// start, or -1.
func matchBalanced(text []byte, start int, openCh, closeCh byte) int {
	depth := 0
	for i := start; i < len(text); i++ {
		if text[i] == openCh {
			depth++
		} else if text[i] == closeCh {
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// findSitesByName: one pass over the tree, bucketed by the name the match
// carries.
func (g *graph) findSitesByName(scan func([]byte) []identMatch, wanted map[string]bool) map[string][]site {
	found := map[string][]site{}
	for name := range wanted {
		found[name] = nil
	}
	for _, source := range g.productFiles {
		for _, m := range scan(source.code) {
			if _, ok := found[m.name]; !ok {
				continue
			}
			found[m.name] = append(found[m.name], site{source.rel, source.lineOf(m.start)})
		}
	}
	return found
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var buf [24]byte
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}
