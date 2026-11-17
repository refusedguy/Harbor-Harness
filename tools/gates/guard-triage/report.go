package guardtriage

// report.go -- the report the tool writes, and the two tables it renders
// beside it.

import (
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
)

// ruleReport is one rule's row of the report.
type ruleReport struct {
	rule         string
	count        int
	byClass      map[string]int
	guardFile    string
	hasGuardFile bool
	guardLine    int
	hasGuardLine bool
	classifiedBy string
	reason       string
	ratchet      string
	evidence     []jsonValue
	heads        map[string]bool
	distinct     int
	noiseShare   float64
	unknownShare float64
}

// buildReport returns the report the tool writes.
func buildReport(
	firings []firing,
	reg *registry,
	index *guardIndex,
	sources map[string][]string,
	window map[string]string,
) jsonValue {
	byClass := map[string]int{}
	for _, name := range classes {
		byClass[name] = 0
	}
	perRule := map[string]*ruleReport{}
	var ruleOrder []string
	distinct := map[string]bool{}

	for _, f := range firings {
		byClass[f.classification]++
		head := headOf(f.evidence)
		distinct[f.rule+"\x00"+head] = true
		rule, ok := perRule[f.rule]
		if !ok {
			rule = &ruleReport{
				rule:         f.rule,
				byClass:      map[string]int{},
				guardFile:    f.guardFile,
				hasGuardFile: f.hasGuardFile,
				guardLine:    f.guardLine,
				hasGuardLine: f.hasGuardLine,
				classifiedBy: f.classifiedBy,
				reason:       f.reason,
				ratchet:      f.ratchet,
			}
			for _, name := range classes {
				rule.byClass[name] = 0
			}
			rule.heads = map[string]bool{}
			perRule[f.rule] = rule
			ruleOrder = append(ruleOrder, f.rule)
		}
		rule.count++
		rule.byClass[f.classification]++
		rule.heads[head] = true
		if !rule.hasGuardFile && f.hasGuardFile {
			rule.guardFile = f.guardFile
			rule.hasGuardFile = true
			rule.guardLine = f.guardLine
			rule.hasGuardLine = f.hasGuardLine
		}
		if len(rule.evidence) < 5 {
			rule.evidence = append(rule.evidence, jsonObject([]jsonMember{
				jsonMemberOf("run_id", jsonString(f.runID)),
				jsonMemberOf("run", f.run),
				jsonMemberOf("origin", jsonString(f.origin)),
				jsonMemberOf("guard_file", optionalString(f.guardFile, f.hasGuardFile)),
				jsonMemberOf("guard_line", optionalInt(f.guardLine, f.hasGuardLine)),
				jsonMemberOf("locations", locationsJSON(f.locations)),
				jsonMemberOf("evidence_head", jsonString(firstLine(f.evidence))),
			}))
		}
	}

	for _, rule := range perRule {
		count := rule.count
		if count == 0 {
			count = 1
		}
		rule.distinct = len(rule.heads)
		rule.noiseShare = round4(float64(rule.byClass["noise"]) / float64(count))
		rule.unknownShare = round4(float64(rule.byClass["unknown"]) / float64(count))
	}

	ordered := make([]*ruleReport, 0, len(perRule))
	for _, name := range ruleOrder {
		ordered = append(ordered, perRule[name])
	}
	sort.SliceStable(ordered, func(i, j int) bool {
		if ordered[i].noiseShare != ordered[j].noiseShare {
			return ordered[i].noiseShare > ordered[j].noiseShare
		}
		if ordered[i].count != ordered[j].count {
			return ordered[i].count > ordered[j].count
		}
		return ordered[i].rule < ordered[j].rule
	})

	total := len(firings)
	noiseShare := 0.0
	unknownShare := 0.0
	classifiedShare := 0.0
	if total > 0 {
		noiseShare = round4(float64(byClass["noise"]) / float64(total))
		unknownShare = round4(float64(byClass["unknown"]) / float64(total))
		classifiedShare = round4(float64(total-byClass["unknown"]) / float64(total))
	}

	rules := make([]jsonValue, 0, len(ordered))
	for _, rule := range ordered {
		classCounts := make([]jsonMember, 0, len(classes))
		for _, name := range classes {
			classCounts = append(classCounts,
				jsonMemberOf(name, jsonIntOf(rule.byClass[name])))
		}
		rules = append(rules, jsonObject([]jsonMember{
			jsonMemberOf("rule", jsonString(rule.rule)),
			jsonMemberOf("count", jsonIntOf(rule.count)),
			jsonMemberOf("by_class", jsonObject(classCounts)),
			jsonMemberOf("guard_file", optionalString(rule.guardFile, rule.hasGuardFile)),
			jsonMemberOf("guard_line", optionalInt(rule.guardLine, rule.hasGuardLine)),
			jsonMemberOf("classified_by", jsonString(rule.classifiedBy)),
			jsonMemberOf("reason", jsonString(rule.reason)),
			jsonMemberOf("ratchet", jsonString(rule.ratchet)),
			jsonMemberOf("evidence", jsonArray(rule.evidence)),
			jsonMemberOf("distinct", jsonIntOf(rule.distinct)),
			jsonMemberOf("noise_share", jsonFloat(rule.noiseShare)),
			jsonMemberOf("unknown_share", jsonFloat(rule.unknownShare)),
		}))
	}

	firingValues := make([]jsonValue, 0, len(firings))
	for _, f := range firings {
		firingValues = append(firingValues, jsonObject([]jsonMember{
			jsonMemberOf("rule", jsonString(f.rule)),
			jsonMemberOf("test_name", jsonString(f.testName)),
			jsonMemberOf("project", jsonString(f.project)),
			jsonMemberOf("class", jsonString(f.classification)),
			jsonMemberOf("classified_by", jsonString(f.classifiedBy)),
			jsonMemberOf("reason", jsonString(f.reason)),
			jsonMemberOf("ratchet", jsonString(f.ratchet)),
			jsonMemberOf("guard_file", optionalString(f.guardFile, f.hasGuardFile)),
			jsonMemberOf("guard_line", optionalInt(f.guardLine, f.hasGuardLine)),
			jsonMemberOf("locations", locationsJSON(f.locations)),
			jsonMemberOf("evidence", jsonString(f.evidence)),
			jsonMemberOf("source", jsonString(f.source)),
			jsonMemberOf("origin", jsonString(f.origin)),
			jsonMemberOf("run_id", jsonString(f.runID)),
			jsonMemberOf("run", f.run),
		}))
	}

	classCounts := make([]jsonMember, 0, len(classes))
	for _, name := range classes {
		classCounts = append(classCounts, jsonMemberOf(name, jsonIntOf(byClass[name])))
	}

	return jsonObject([]jsonMember{
		jsonMemberOf("schema", jsonString(schema)),
		jsonMemberOf("window", jsonObject([]jsonMember{
			jsonMemberOf("from", jsonString(window["from"])),
			jsonMemberOf("to", jsonString(window["to"])),
			jsonMemberOf("runs", jsonIntOf(windowRuns(window))),
		})),
		jsonMemberOf("sources", jsonObject([]jsonMember{
			jsonMemberOf("trx", jsonStrings(sources["trx"])),
			jsonMemberOf("logs", jsonStrings(sources["logs"])),
		})),
		jsonMemberOf("totals", jsonObject([]jsonMember{
			jsonMemberOf("firings", jsonIntOf(total)),
			jsonMemberOf("distinct_firings", jsonIntOf(len(distinct))),
			jsonMemberOf("rules", jsonIntOf(len(perRule))),
			jsonMemberOf("by_class", jsonObject(classCounts)),
			jsonMemberOf("noise_share", jsonFloat(noiseShare)),
			jsonMemberOf("unknown_share", jsonFloat(unknownShare)),
			jsonMemberOf("classified_share", jsonFloat(classifiedShare)),
		})),
		jsonMemberOf("rules", jsonArray(rules)),
		jsonMemberOf("firings", jsonArray(firingValues)),
	})
}

// windowRuns is the number of runs the window holds.
func windowRuns(window map[string]string) int {
	n := 0
	if v, ok := window["runs"]; ok {
		n, _ = strconv.Atoi(v)
	}
	return n
}

// locationsJSON renders the locations of a firing.
func locationsJSON(locations []location) jsonValue {
	items := make([]jsonValue, 0, len(locations))
	for _, loc := range locations {
		items = append(items, jsonObject([]jsonMember{
			jsonMemberOf("path", jsonString(loc.path)),
			jsonMemberOf("line", optionalInt(loc.line, loc.has)),
		}))
	}
	return jsonArray(items)
}

// optionalInt is a number, or null when the value is absent (python's
// None).
func optionalInt(n int, present bool) jsonValue {
	if !present {
		return jsonNull()
	}
	return jsonIntOf(n)
}

// optionalString is a string, or null when the value is absent (python's
// None).
func optionalString(s string, present bool) jsonValue {
	if !present {
		return jsonNull()
	}
	return jsonString(s)
}

// jsonStrings renders a list of strings.
func jsonStrings(list []string) jsonValue {
	items := make([]jsonValue, 0, len(list))
	for _, item := range list {
		items = append(items, jsonString(item))
	}
	return jsonArray(items)
}

// firstLine is the first line of the evidence.
func firstLine(evidence string) string {
	if evidence == "" {
		return ""
	}
	lines := splitLines(evidence)
	if len(lines) == 0 {
		return ""
	}
	return lines[0]
}

// round4 is python's `round(value, 4)`: the value is rounded to four
// decimals, with a tie going to the even digit, and the result is a float
// again.
func round4(value float64) float64 {
	text := strconv.FormatFloat(value, 'f', 4, 64)
	parsed, err := strconv.ParseFloat(text, 64)
	if err != nil {
		return value
	}
	return parsed
}

// jsonFloat renders a float the way python's json does: the shortest
// representation that round-trips, and a ".0" on an integral value.
func jsonFloat(value float64) jsonValue {
	return jsonValue{kind: '#', num: json.Number(renderFloat(value))}
}

// renderFloat is python's `repr(float)`.
func renderFloat(value float64) string {
	if value == math.Trunc(value) && math.Abs(value) < 1e16 {
		return strconv.FormatFloat(value, 'f', 1, 64)
	}
	text := strconv.FormatFloat(value, 'g', -1, 64)
	if !strings.ContainsAny(text, "eE") {
		return text
	}
	// Python writes the exponent with at least two digits and a sign.
	mantissa, exponent, found := strings.Cut(text, "e")
	if !found {
		mantissa, exponent, found = strings.Cut(text, "E")
	}
	if !found {
		return text
	}
	sign := "+"
	if strings.HasPrefix(exponent, "-") {
		sign = "-"
		exponent = exponent[1:]
	} else if strings.HasPrefix(exponent, "+") {
		exponent = exponent[1:]
	}
	for len(exponent) < 2 {
		exponent = "0" + exponent
	}
	return mantissa + "e" + sign + exponent
}

// reportTotals is the totals block of a report.
func reportTotals(report jsonValue) jsonValue {
	for _, member := range report.obj {
		if member.key == "totals" {
			return member.value
		}
	}
	return jsonObject(nil)
}

// totalsInt is a number of the totals block.
func totalsInt(totals jsonValue, key string) int {
	for _, member := range totals.obj {
		if member.key == key && member.value.kind == '#' {
			n, _ := strconv.Atoi(member.value.num.String())
			return n
		}
	}
	return 0
}

// totalsClass is a class count of the totals block.
func totalsClass(totals jsonValue, name string) int {
	for _, member := range totals.obj {
		if member.key != "by_class" {
			continue
		}
		for _, class := range member.value.obj {
			if class.key == name && class.value.kind == '#' {
				n, _ := strconv.Atoi(class.value.num.String())
				return n
			}
		}
	}
	return 0
}

// renderMarkdown is the markdown report.
func renderMarkdown(report jsonValue) string {
	totals := reportTotals(report)
	var lines []string
	lines = append(lines, "# Guard firings, classified")
	lines = append(lines, "")
	window := map[string]string{}
	for _, member := range report.obj {
		if member.key != "window" {
			continue
		}
		for _, item := range member.value.obj {
			if item.value.kind == 's' {
				window[item.key] = item.value.str
			}
		}
	}
	// The python original uses `window.get('from', '?')`: the fallback is
	// for a MISSING key, and the key is always there — an empty window
	// prints two empty strings, not two question marks.
	lines = append(lines, fmt.Sprintf("- window: %s .. %s", window["from"], window["to"]))
	lines = append(lines, fmt.Sprintf("- firings: %d across %d rules",
		totalsInt(totals, "firings"), totalsInt(totals, "rules")))
	lines = append(lines, fmt.Sprintf("- distinct firings (rule + evidence): %d",
		totalsInt(totals, "distinct_firings")))
	var classParts []string
	for _, name := range classes {
		classParts = append(classParts, fmt.Sprintf("%s %d", name, totalsClass(totals, name)))
	}
	lines = append(lines, "- by class: "+strings.Join(classParts, ", "))
	lines = append(lines, fmt.Sprintf("- noise share: %.1f%%", totalsFloat(totals, "noise_share")*100))
	lines = append(lines, fmt.Sprintf("- unknown share: %.1f%%", totalsFloat(totals, "unknown_share")*100))
	lines = append(lines, "")
	lines = append(lines, "| rule | firings | distinct | defect | style | noise | unknown | noise share | ratchet |")
	lines = append(lines, "|---|---:|---:|---:|---:|---:|---:|---:|---|")
	for _, rule := range reportRules(report) {
		counts := ruleByClass(rule)
		lines = append(lines, fmt.Sprintf(
			"| `%s` | %d | %d | %d | %d | %d | %d | %.0f%% | %s |",
			ruleString(rule, "rule"),
			ruleInt(rule, "count"),
			ruleInt(rule, "distinct"),
			counts["defect"], counts["style"], counts["noise"], counts["unknown"],
			ruleFloat(rule, "noise_share")*100,
			orDash(ruleString(rule, "ratchet"))))
	}
	lines = append(lines, "")
	return strings.Join(lines, "\n")
}

// renderSummary is the table that goes into a CI job summary.
func renderSummary(report jsonValue) string {
	totals := reportTotals(report)
	var lines []string
	lines = append(lines, "| class | firings |")
	lines = append(lines, "|---|---:|")
	for _, name := range classes {
		lines = append(lines, fmt.Sprintf("| %s | %d |", name, totalsClass(totals, name)))
	}
	lines = append(lines, fmt.Sprintf("| **total** | **%d** |", totalsInt(totals, "firings")))
	lines = append(lines, "")
	rules := reportRules(report)
	if len(rules) > 0 {
		lines = append(lines, "| rule | firings | noise | unknown |")
		lines = append(lines, "|---|---:|---:|---:|")
		for i, rule := range rules {
			if i >= 15 {
				break
			}
			counts := ruleByClass(rule)
			lines = append(lines, fmt.Sprintf("| `%s` | %d | %d | %d |",
				ruleString(rule, "rule"), ruleInt(rule, "count"),
				counts["noise"], counts["unknown"]))
		}
	}
	return strings.Join(lines, "\n")
}

// orDash is the value, or a dash when it is empty.
func orDash(value string) string {
	if value == "" {
		return "-"
	}
	return value
}

// reportRules is the rules array of a report.
func reportRules(report jsonValue) []jsonValue {
	for _, member := range report.obj {
		if member.key == "rules" && member.value.kind == 'a' {
			return member.value.arr
		}
	}
	return nil
}

// ruleByClass is the class counts of a rule row.
func ruleByClass(rule jsonValue) map[string]int {
	out := map[string]int{}
	for _, member := range rule.obj {
		if member.key != "by_class" {
			continue
		}
		for _, class := range member.value.obj {
			n, _ := strconv.Atoi(class.value.num.String())
			out[class.key] = n
		}
	}
	return out
}

// ruleString is a string member of a rule row.
func ruleString(rule jsonValue, key string) string {
	for _, member := range rule.obj {
		if member.key == key && member.value.kind == 's' {
			return member.value.str
		}
	}
	return ""
}

// ruleInt is a number member of a rule row.
func ruleInt(rule jsonValue, key string) int {
	for _, member := range rule.obj {
		if member.key == key && member.value.kind == '#' {
			n, _ := strconv.Atoi(member.value.num.String())
			return n
		}
	}
	return 0
}

// ruleFloat is a float member of a rule row.
func ruleFloat(rule jsonValue, key string) float64 {
	for _, member := range rule.obj {
		if member.key == key && member.value.kind == '#' {
			n, _ := strconv.ParseFloat(member.value.num.String(), 64)
			return n
		}
	}
	return 0
}

// totalsFloat is a float of the totals block.
func totalsFloat(totals jsonValue, key string) float64 {
	for _, member := range totals.obj {
		if member.key == key && member.value.kind == '#' {
			n, _ := strconv.ParseFloat(member.value.num.String(), 64)
			return n
		}
	}
	return 0
}
