package testdup

// gate.go -- the gate's whole decision.
//
// A group is matched to the baseline by its MEMBERS, not by the text of its
// body. The text changes every time somebody edits a duplicated test -- and
// an edit that changes the text of ONE copy breaks the duplication, while an
// edit that changes the text of ALL copies keeps it and must not be reported
// as new duplication.
//
// The members carry the PATH, and a move changes it -- so the members alone
// are not enough: the second matcher closes that hole: the same SHAPE and
// the same METHOD NAMES mean the same duplication, wherever its files now
// live.

import (
	"fmt"
	"time"
)

// gateResult is the gate's whole decision.
type gateResult struct {
	findings  []string
	warnings  []string
	groups    []*dupGroup
	measured  censusCounts
	offenders []*dupGroup
}

// today is the current date, as the expiry comparison reads it.
func today() string {
	return time.Now().Format("2006-01-02")
}

// memberNames is the multiset of method names behind a set of member keys.
//
// A member key is `path::method#ordinal`. The PATH is what a move changes;
// the ORDINAL is what an inserted sibling changes; the NAME is what
// identifies the duplicated body.
func memberNames(memberKeys []string) map[string]int {
	counts := map[string]int{}
	for _, key := range memberKeys {
		name := key
		if index := indexOf(name, "::"); index >= 0 {
			name = name[index+2:]
		}
		if index := indexOf(name, "#"); index >= 0 {
			name = name[:index]
		}
		counts[name]++
	}
	return counts
}

// indexOf returns the index of a substring, or -1.
func indexOf(text string, sub string) int {
	for i := 0; i+len(sub) <= len(text); i++ {
		if text[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}

// covers reports whether the multiset `big` holds every element of `small`.
func covers(big map[string]int, small map[string]int) bool {
	for name, count := range small {
		if big[name] < count {
			return false
		}
	}
	return true
}

// relocationMatch is the baseline entry that knows this group at other
// paths, or nil.
//
// Two facts survive the move, and both have to hold: the SHAPE of the body,
// and the multiset of METHOD NAMES.
//
// What does NOT survive is an ADDED copy: the baseline's names do not cover
// the group's, and the growth branch reports it.
func relocationMatch(group *dupGroup, byShape map[string][]*baselineGroup) *baselineGroup {
	names := memberNames(group.memberKeys)
	for _, entry := range byShape[group.fingerprint] {
		baseNames := memberNames(entry.Members)
		if covers(baseNames, names) {
			return entry
		}
	}
	return nil
}

// growthMatch is the baseline entry this group grew from, or nil.
//
// The same body, at other paths, with copies ADDED. The growth is the new
// duplication; the relocation is not.
func growthMatch(group *dupGroup, byShape map[string][]*baselineGroup) *baselineGroup {
	names := memberNames(group.memberKeys)
	var best *baselineGroup
	for _, entry := range byShape[group.fingerprint] {
		baseNames := memberNames(entry.Members)
		if len(baseNames) >= len(names) {
			continue
		}
		if !covers(names, baseNames) {
			continue
		}
		if best == nil || len(baseNames) > best.entryCount() {
			best = entry
		}
	}
	return best
}

// runGate runs the gate over a tree.
func runGate(root string, minLines int, minMembers int, minSimilarity float64,
	minFiles int, minMethods int, changedFiles map[string]bool) gateResult {
	methods, scan := scanTree(root)
	groups, coincidences := buildGroups(methods, minLines, minMembers, minSimilarity)

	narrowed := changedFiles != nil
	if narrowed {
		var kept []*dupGroup
		for _, group := range groups {
			for _, member := range group.members {
				if changedFiles[member.path] {
					kept = append(kept, group)
					break
				}
			}
		}
		groups = kept
	}

	measured := census(groups, scan, coincidences)
	result := gateResult{groups: groups, measured: measured}

	baseline := loadBaseline(root)
	if baseline == nil {
		result.findings = append(result.findings, fmt.Sprintf(
			"DUP-BASELINE-MISSING %s: no baseline committed under tools/ — the gate passes on nothing. "+
				"Generate one with `harbor-gates check-test-dup --update` and commit it.",
			baselineName))
		return result
	}

	entries := baseline.groupEntries()
	rows := exemptRows(baseline.Exemptions)
	for index, row := range rows {
		if row.reason == "" {
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-EXEMPT-INVALID exemption #%d (%s) has no reason — an allowance with no reader is a hole",
				index, joinPatterns(row.patterns)))
		}
		if row.expires == "" {
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-EXEMPT-INVALID exemption #%d (%s) has no expiry — an allowance that never ends is not an allowance",
				index, joinPatterns(row.patterns)))
		}
	}

	// An exempt group is reported but not failed: the zones other programmes
	// own are being migrated by them.
	used := map[int]bool{}
	var blocking []*dupGroup
	for _, group := range groups {
		cover := groupExempt(group, rows)
		if cover >= 0 {
			used[cover] = true
			continue
		}
		blocking = append(blocking, group)
	}

	byShape := map[string][]*baselineGroup{}
	for _, entry := range entries {
		byShape[entry.Shape] = append(byShape[entry.Shape], entry)
	}

	for _, group := range blocking {
		members := map[string]bool{}
		for _, key := range group.memberKeys {
			members[key] = true
		}
		var subsetOf, supersetOf, sameShapeOf *baselineGroup
		for _, entry := range entries {
			baseMembers := map[string]bool{}
			for _, key := range entry.Members {
				baseMembers[key] = true
			}
			if isSubset(members, baseMembers) {
				subsetOf = entry
				break
			}
			if isStrictSubset(baseMembers, members) {
				supersetOf = entry
			}
			if sameShapeOf == nil && len(baseMembers) == len(members) {
				files := map[string]bool{}
				for key := range baseMembers {
					files[keyPrefix(key)] = true
				}
				if len(files) == group.fileCount {
					sameShapeOf = entry
				}
			}
		}
		if subsetOf != nil {
			// Known: the same methods are still duplicated.
			continue
		}
		if relocationMatch(group, byShape) != nil {
			// Known: the same duplication, relocated.
			continue
		}
		growthOf := growthMatch(group, byShape)
		result.offenders = append(result.offenders, group)
		head := group.members[0].where()
		switch {
		case supersetOf != nil:
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-NEW %s x %d copies (%d code lines each) — the baseline recorded %v copies of these bodies; %d more were added",
				head, group.count, group.lines, countValue(supersetOf), group.count-countValue(supersetOf)))
		case growthOf != nil:
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-NEW %s x %d copies (%d code lines each) — the baseline recorded %d copies of these bodies under other paths; %d more were added",
				head, group.count, group.lines, growthOf.entryCount(), group.count-growthOf.entryCount()))
		case sameShapeOf != nil:
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-NEW %s x %d copies (%d code lines each) — the baseline has a group with the same %d file(s) and the same count under other names; if the bodies were rewritten on purpose, regenerate the baseline with `--update`",
				head, group.count, group.lines, group.fileCount))
		default:
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-NEW %s x %d copies (%d code lines each) — no such group in %s; this body is new duplication",
				head, group.count, group.lines, baselineName))
		}
	}

	// The aggregate comparisons only make sense over the WHOLE tree: a
	// narrowed run sees a fraction of the duplication.
	if !narrowed {
		blockingLines := 0
		for _, group := range blocking {
			blockingLines += group.duplicateLines
		}
		if baseline.BlockingDuplicateLines != nil && blockingLines > *baseline.BlockingDuplicateLines {
			result.findings = append(result.findings, fmt.Sprintf(
				"DUP-GROWTH duplicate lines %d > baseline %d — the tree grew more duplication than the baseline allows",
				blockingLines, *baseline.BlockingDuplicateLines))
		}
		if baseline.Metrics.DuplicateLines != nil &&
			measured.duplicateLines < *baseline.Metrics.DuplicateLines {
			result.warnings = append(result.warnings, fmt.Sprintf(
				"DUP-BASELINE-STALE duplicate lines %d < baseline %d — the tree got cleaner; lower the baseline with `--update` when the programme lands",
				measured.duplicateLines, *baseline.Metrics.DuplicateLines))
		}
	}

	if minFiles != 0 && scan.files < minFiles {
		result.findings = append(result.findings, fmt.Sprintf(
			"DUP-FLOOR files %d < --min-files %d — the scanner sees less of the tree than it should",
			scan.files, minFiles))
	}
	if minMethods != 0 && scan.methods < minMethods {
		result.findings = append(result.findings, fmt.Sprintf(
			"DUP-FLOOR methods %d < --min-methods %d — the scanner sees less of the tree than it should",
			scan.methods, minMethods))
	}

	for index, row := range rows {
		if used[index] {
			if row.expires != "" && row.expires < today() {
				result.findings = append(result.findings, fmt.Sprintf(
					"DUP-EXEMPT-EXPIRED exemption #%d (%s) expired on %s — renew it in writing or remove it",
					index, joinPatterns(row.patterns), row.expires))
			}
			continue
		}
		if !narrowed {
			// A narrowed run sees a fraction of the tree, so "matches
			// nothing" says nothing about the exemption.
			result.warnings = append(result.warnings, fmt.Sprintf(
				"DUP-EXEMPT-UNUSED exemption #%d (%s) matches no duplicate group — remove it rather than let it outlive its reason",
				index, joinPatterns(row.patterns)))
		}
	}

	return result
}

// isSubset reports whether every member of `left` is in `right`.
func isSubset(left map[string]bool, right map[string]bool) bool {
	for key := range left {
		if !right[key] {
			return false
		}
	}
	return true
}

// isStrictSubset reports whether `left` is inside `right` and smaller.
func isStrictSubset(left map[string]bool, right map[string]bool) bool {
	return isSubset(left, right) && len(left) < len(right)
}

// keyPrefix is the path of a member key.
func keyPrefix(key string) string {
	if index := indexOf(key, "::"); index >= 0 {
		return key[:index]
	}
	return key
}

// countValue is the count of a baseline entry, or zero when it has none.
func countValue(entry *baselineGroup) int {
	if entry.Count == nil {
		return 0
	}
	return *entry.Count
}

// joinPatterns renders the patterns of an exemption.
func joinPatterns(patterns []string) string {
	out := ""
	for index, pattern := range patterns {
		if index > 0 {
			out += ", "
		}
		out += pattern
	}
	return out
}
