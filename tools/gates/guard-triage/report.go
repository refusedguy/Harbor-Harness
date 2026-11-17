package guardtriage

// report.go -- classify the firings, dedupe them, and write the per-rule
// report. The order of the four classes, the order of the rules and the
// ratchets are the ones the python original used.

import (
	"fmt"
	"sort"
	"strings"

	"harbor.local/gates/internal/pyfmt"
)

// classCounts keeps the four class counts in report order.
type classCounts struct {
	defect  int
	style   int
	noise   int
	unknown int
}

func (c *classCounts) inc(name string) {
	switch name {
	case "defect":
		c.defect++
	case "style":
		c.style++
	case "noise":
		c.noise++
	default:
		c.unknown++
	}
}

func (c *classCounts) get(name string) int {
	switch name {
	case "defect":
		return c.defect
	case "style":
		return c.style
	case "noise":
		return c.noise
	default:
		return c.unknown
	}
}

func (c *classCounts) toNode() *jnode {
	node := jobject()
	for _, name := range classes {
		node.set(name, jint(c.get(name)))
	}
	return node
}

type ruleAcc struct {
	rule         string
	count        int
	byClass      classCounts
	guardFile    string
	guardLine    int
	classifiedBy string
	reason       string
	ratchet      string
	evidence     []*jnode
	distinct     map[string]bool
	// computed
	distinctCount int
	noiseShare    float64
	unknownShare  float64
}

type builtReport struct {
	windowFrom string
	windowTo   string
	windowRuns int
	trxUsed    []string
	logsUsed   []string
	firings    []*firing
	rules      []*ruleAcc
	byClass    classCounts
	distinct   int
	total      int
}

// classify fills in classification / reason / ratchet for one firing, in the
// same order the python original used: per-firing, per-rule, the two
// mechanical detectors, then unknown.
func classify(f *firing, reg *registry, index *guardIndex, allowances []allowance) {
	for _, entry := range reg.firings {
		if entry.rule != f.rule {
			continue
		}
		if entry.evidenceContains != "" && !strings.Contains(f.evidence, entry.evidenceContains) {
			continue
		}
		class := entry.class
		if class == "" {
			class = "unknown"
		}
		f.classification = class
		f.classifiedBy = "registry:firing"
		f.reason = entry.reason
		f.ratchet = entry.ratchet
		return
	}

	if entry := reg.rules[f.rule]; entry != nil {
		f.classification = entry.class
		f.classifiedBy = "registry:rule"
		f.reason = entry.reason
		f.ratchet = entry.ratchet
		return
	}

	guardSource := ""
	if f.guardFile != "" {
		guardSource = index.source[f.guardFile]
	}
	if guardSource != "" {
		for _, place := range f.locations {
			if strings.Contains(guardSource, place.path) && ledgerStaleRE.MatchString(f.evidence) {
				f.classification = "noise"
				f.classifiedBy = "detector:ledger-retirement"
				f.reason = fmt.Sprintf("the guard's own source lists %s as allowed code and the assertion says the allowance is stale: the rule fired on its own allowance", place.path)
				f.ratchet = "ledger-edit"
				return
			}
		}
	}

	for _, place := range f.locations {
		for _, item := range allowances {
			if strings.Contains(item.text, place.path) {
				f.classification = "noise"
				f.classifiedBy = "detector:allowance-listed"
				f.reason = fmt.Sprintf("%s is named in %s: the repository allows that code, so the firing carries no new information", place.path, item.path)
				f.ratchet = "ledger-edit"
				return
			}
		}
	}

	f.classification = "unknown"
	f.classifiedBy = "none"
	f.reason = "no registry entry and no mechanical signal: needs a human decision"
}

func firstLine(text string) string {
	lines := splitLines(text)
	if len(lines) == 0 {
		return ""
	}
	return lines[0]
}

func headOf(f *firing) string {
	return strings.Join(strings.Fields(firstLine(f.evidence)), " ")
}

type dedupeKey struct {
	runID string
	rule  string
	head  string
}

// dedupe keeps one firing per (run, rule, evidence). A guard failure seen in
// both the in-build gate and the shard is one firing, not two.
func dedupe(firings []*firing) []*firing {
	merged := map[dedupeKey]*firing{}
	var order []dedupeKey
	for _, f := range firings {
		key := dedupeKey{runID: f.runID, rule: f.rule, head: headOf(f)}
		existing, ok := merged[key]
		if !ok {
			merged[key] = f
			order = append(order, key)
			continue
		}
		existing.origin = existing.origin + ", " + f.origin
		if existing.guardFile == "" && f.guardFile != "" {
			existing.guardFile = f.guardFile
			existing.guardLine = f.guardLine
		}
	}
	out := make([]*firing, 0, len(order))
	for _, key := range order {
		out = append(out, merged[key])
	}
	return out
}

func locationNodes(locations []location) *jnode {
	node := jarray()
	for _, place := range locations {
		item := jobject()
		item.set("path", jstring(place.path))
		if place.line == 0 {
			item.set("line", jnull())
		} else {
			item.set("line", jint(place.line))
		}
		node.add(item)
	}
	return node
}

// buildReport assembles the report from already-classified firings.
func buildReport(firings []*firing, trxUsed []string, logsUsed []string, windowFrom string, windowTo string, windowRuns int) *builtReport {
	byClass := classCounts{}
	perRule := map[string]*ruleAcc{}
	var ruleOrder []string
	distinct := map[string]bool{}
	for _, f := range firings {
		byClass.inc(f.classification)
		distinct[f.rule+"\x00"+headOf(f)] = true
		acc, ok := perRule[f.rule]
		if !ok {
			acc = &ruleAcc{
				rule:         f.rule,
				guardFile:    f.guardFile,
				guardLine:    f.guardLine,
				classifiedBy: f.classifiedBy,
				reason:       f.reason,
				ratchet:      f.ratchet,
				distinct:     map[string]bool{},
			}
			perRule[f.rule] = acc
			ruleOrder = append(ruleOrder, f.rule)
		}
		acc.count++
		acc.byClass.inc(f.classification)
		acc.distinct[headOf(f)] = true
		if acc.guardFile == "" && f.guardFile != "" {
			acc.guardFile = f.guardFile
			acc.guardLine = f.guardLine
		}
		if len(acc.evidence) < 5 {
			ev := jobject()
			ev.set("run_id", jstring(f.runID))
			ev.set("run", f.run)
			ev.set("origin", jstring(f.origin))
			if f.guardFile == "" {
				ev.set("guard_file", jnull())
			} else {
				ev.set("guard_file", jstring(f.guardFile))
			}
			if f.guardLine == 0 {
				ev.set("guard_line", jnull())
			} else {
				ev.set("guard_line", jint(f.guardLine))
			}
			ev.set("locations", locationNodes(f.locations))
			ev.set("evidence_head", jstring(firstLine(f.evidence)))
			acc.evidence = append(acc.evidence, ev)
		}
	}

	rules := make([]*ruleAcc, 0, len(ruleOrder))
	for _, name := range ruleOrder {
		acc := perRule[name]
		count := acc.count
		if count == 0 {
			count = 1
		}
		acc.distinctCount = len(acc.distinct)
		acc.noiseShare = pyfmt.Round(float64(acc.byClass.noise)/float64(count), 4)
		acc.unknownShare = pyfmt.Round(float64(acc.byClass.unknown)/float64(count), 4)
		rules = append(rules, acc)
	}
	sort.SliceStable(rules, func(i, j int) bool {
		if rules[i].noiseShare != rules[j].noiseShare {
			return rules[i].noiseShare > rules[j].noiseShare
		}
		if rules[i].count != rules[j].count {
			return rules[i].count > rules[j].count
		}
		return rules[i].rule < rules[j].rule
	})

	return &builtReport{
		windowFrom: windowFrom,
		windowTo:   windowTo,
		windowRuns: windowRuns,
		trxUsed:    trxUsed,
		logsUsed:   logsUsed,
		firings:    firings,
		rules:      rules,
		byClass:    byClass,
		distinct:   len(distinct),
		total:      len(firings),
	}
}

func (br *builtReport) noiseShare() float64 {
	if br.total == 0 {
		return 0.0
	}
	return pyfmt.Round(float64(br.byClass.noise)/float64(br.total), 4)
}

func (br *builtReport) unknownShare() float64 {
	if br.total == 0 {
		return 0.0
	}
	return pyfmt.Round(float64(br.byClass.unknown)/float64(br.total), 4)
}

func (br *builtReport) classifiedShare() float64 {
	if br.total == 0 {
		return 0.0
	}
	return pyfmt.Round(float64(br.total-br.byClass.unknown)/float64(br.total), 4)
}

func stringsNode(values []string) *jnode {
	node := jarray()
	for _, value := range values {
		node.add(jstring(value))
	}
	return node
}

// reportJSON builds the machine-readable report, with keys in the order the
// python original's dicts had them.
func (br *builtReport) reportJSON() *jnode {
	node := jobject()
	node.set("schema", jstring(schema))

	window := jobject()
	window.set("from", jstring(br.windowFrom))
	window.set("to", jstring(br.windowTo))
	window.set("runs", jint(br.windowRuns))
	node.set("window", window)

	sources := jobject()
	sources.set("trx", stringsNode(br.trxUsed))
	sources.set("logs", stringsNode(br.logsUsed))
	node.set("sources", sources)

	totals := jobject()
	totals.set("firings", jint(br.total))
	totals.set("distinct_firings", jint(br.distinct))
	totals.set("rules", jint(len(br.rules)))
	totals.set("by_class", br.byClass.toNode())
	totals.set("noise_share", jfloat(br.noiseShare()))
	totals.set("unknown_share", jfloat(br.unknownShare()))
	totals.set("classified_share", jfloat(br.classifiedShare()))
	node.set("totals", totals)

	rulesNode := jarray()
	for _, acc := range br.rules {
		rule := jobject()
		rule.set("rule", jstring(acc.rule))
		rule.set("count", jint(acc.count))
		rule.set("by_class", acc.byClass.toNode())
		if acc.guardFile == "" {
			rule.set("guard_file", jnull())
		} else {
			rule.set("guard_file", jstring(acc.guardFile))
		}
		if acc.guardLine == 0 {
			rule.set("guard_line", jnull())
		} else {
			rule.set("guard_line", jint(acc.guardLine))
		}
		rule.set("classified_by", jstring(acc.classifiedBy))
		rule.set("reason", jstring(acc.reason))
		rule.set("ratchet", jstring(acc.ratchet))
		evidence := jarray()
		for _, ev := range acc.evidence {
			evidence.add(ev)
		}
		rule.set("evidence", evidence)
		rule.set("distinct", jint(acc.distinctCount))
		rule.set("noise_share", jfloat(acc.noiseShare))
		rule.set("unknown_share", jfloat(acc.unknownShare))
		rulesNode.add(rule)
	}
	node.set("rules", rulesNode)

	firingsNode := jarray()
	for _, f := range br.firings {
		firingsNode.add(f.toJSON())
	}
	node.set("firings", firingsNode)
	return node
}

func percent(value float64, digits int) string {
	return fmt.Sprintf("%."+fmt.Sprint(digits)+"f%%", value*100)
}

// renderMarkdown is the table that goes into a report file.
func renderMarkdown(br *builtReport) string {
	lines := []string{
		"# Guard firings, classified",
		"",
		fmt.Sprintf("- window: %s .. %s", br.windowFrom, br.windowTo),
		fmt.Sprintf("- firings: %d across %d rules", br.total, len(br.rules)),
		fmt.Sprintf("- distinct firings (rule + evidence): %d", br.distinct),
	}
	byClass := make([]string, 0, len(classes))
	for _, name := range classes {
		byClass = append(byClass, fmt.Sprintf("%s %d", name, br.byClass.get(name)))
	}
	lines = append(lines, "- by class: "+strings.Join(byClass, ", "))
	lines = append(lines,
		fmt.Sprintf("- noise share: %s", percent(br.noiseShare(), 1)),
		fmt.Sprintf("- unknown share: %s", percent(br.unknownShare(), 1)),
		"",
		"| rule | firings | distinct | defect | style | noise | unknown | noise share | ratchet |",
		"|---|---:|---:|---:|---:|---:|---:|---:|---|",
	)
	for _, acc := range br.rules {
		ratchet := acc.ratchet
		if ratchet == "" {
			ratchet = "-"
		}
		lines = append(lines, fmt.Sprintf("| `%s` | %d | %d | %d | %d | %d | %d | %s | %s |",
			acc.rule, acc.count, acc.distinctCount,
			acc.byClass.defect, acc.byClass.style, acc.byClass.noise, acc.byClass.unknown,
			percent(acc.noiseShare, 0), ratchet))
	}
	lines = append(lines, "")
	return strings.Join(lines, "\n")
}

// renderSummary is the table that goes into a CI job summary.
func renderSummary(br *builtReport) string {
	lines := []string{"| class | firings |", "|---|---:|"}
	for _, name := range classes {
		lines = append(lines, fmt.Sprintf("| %s | %d |", name, br.byClass.get(name)))
	}
	lines = append(lines, fmt.Sprintf("| **total** | **%d** |", br.total), "")
	if len(br.rules) > 0 {
		lines = append(lines,
			"| rule | firings | noise | unknown |",
			"|---|---:|---:|---:|",
		)
		limit := len(br.rules)
		if limit > 15 {
			limit = 15
		}
		for _, acc := range br.rules[:limit] {
			lines = append(lines, fmt.Sprintf("| `%s` | %d | %d | %d |",
				acc.rule, acc.count, acc.byClass.noise, acc.byClass.unknown))
		}
	}
	return strings.Join(lines, "\n")
}
