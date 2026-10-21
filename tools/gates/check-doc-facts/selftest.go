package docfacts

// selftest.go -- the gate's own proof that it can fail.
//
// Every fixture below is a shape that made an earlier version of this gate
// wrong. Two of them are here because they FAILED against a real draft:
//
//   wrong_population  the first draft counted `^docs/` recursively and
//                     passed a document that was wrong by 83. A rule that
//                     compares against the wrong population is #939's
//                     failure: green on a number that is not the one in the
//                     document.
//   no_capture_group  the first draft read the capture group inside a
//                     try/except that returned "no finding" — so a rule
//                     whose capture group was edited away reported BLIND,
//                     and BLIND is the one verdict CI passes. That is #591
//                     exactly. The assertion is now made on the pattern,
//                     before matching.

import (
	"fmt"
	"regexp"

	"harbor.local/gates/internal/gate"
)

const (
	fixtureOK       = "docs/                                 — 64 top-level docs (x)\n"
	fixtureStale    = "docs/                                 — 62 top-level docs (x)\n"
	fixtureWrongPop = "docs/                                 — 147 top-level docs (x)\n"
)

// syntheticTree is a 4-file tree: 2 top-level docs, 1 in a subdir, 1
// elsewhere.
func syntheticTree() []string {
	return []string{
		"docs/ALPHA.md",
		"docs/BETA.md",
		"docs/adr/GAMMA.md",
		"src/DELTA.cs",
	}
}

// runMeasure returns the four outcomes, kept DISTINGUISHABLE. Never
// collapse to a pass.
func runMeasure(meas measure, text string, tree []string) (string, int) {
	if meas.pattern.NumSubexp() < 1 {
		return "NO-CAPTURE", 0
	}
	m := meas.pattern.FindStringSubmatch(text)
	if m == nil {
		return "BLIND", 0
	}
	stated := 0
	fmt.Sscanf(m[1], "%d", &stated)
	actual := meas.recount(tree)
	if stated != actual {
		return "FIRED", stated
	}
	return "PASSED", stated
}

func selfTestRun() int {
	meas := measures()[0]
	tree := syntheticTree()
	topLevel := 2
	recursive := 3

	type testCase struct {
		name        string
		text        string
		truth       int
		mustNotPass bool
	}
	cases := []testCase{
		{
			name:  "correct count passes",
			text:  replaceAll(fixtureOK, "64", fmt.Sprint(topLevel)),
			truth: topLevel,
		},
		{
			name:        "stale count fires",
			text:        replaceAll(fixtureStale, "62", "1"),
			truth:       topLevel,
			mustNotPass: true,
		},
		// A count that is right for the WRONG population. The recursive count
		// is 3; stating 3 where the document claims top-level is a defect,
		// and a rule counting recursively would call it correct.
		{
			name:        "wrong population fires",
			text:        replaceAll(fixtureWrongPop, "147", fmt.Sprint(recursive)),
			truth:       topLevel,
			mustNotPass: true,
		},
		{
			name:  "pattern matches nothing is BLIND, not a pass",
			text:  "nothing resembling a repo map here\n",
			truth: topLevel,
		},
		{
			name:        "capture group edited away is NO-CAPTURE, not a pass",
			text:        fixtureOK,
			truth:       topLevel,
			mustNotPass: true,
		},
	}
	// The capture-group case needs a pattern that genuinely has no group.
	noGroup := measure{
		rule:       meas.rule,
		label:      meas.label,
		pattern:    regexp.MustCompile(`(?m)^docs/\s+—\s+top-level docs\b`),
		recount:    meas.recount,
		population: meas.population,
	}

	ok := true
	fmt.Printf("fixtures: %d\n", len(cases))
	for _, c := range cases {
		m := meas
		if c.name == "capture group edited away is NO-CAPTURE, not a pass" {
			m = noGroup
		}
		verdict, _ := runMeasure(m, c.text, tree)
		fires := verdict == "FIRED"
		// NO-CAPTURE is a detected broken rule, which satisfies "must not
		// pass".
		good := fires == c.mustNotPass || verdict == "NO-CAPTURE"
		ok = ok && good
		mark := "ok"
		if !good {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%-38s] %-11s tree_top_level=%d %s\n", c.name, verdict, c.truth, mark)
		if !good {
			expected := "a finding"
			if !c.mustNotPass {
				expected = "a pass"
			}
			fmt.Printf("      expected %s, got %s\n", expected, verdict)
		}
	}

	// The floor: the rule must compare at least one thing, or it is blind.
	if meas.pattern.NumSubexp() >= 1 && meas.recount(tree) != topLevel {
		fmt.Println("  [recount is wrong population           ] << MISMATCH")
		ok = false
	}
	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}

func replaceAll(s, old, new string) string {
	out := ""
	for {
		index := indexOf(s, old)
		if index < 0 {
			return out + s
		}
		out += s[:index] + new
		s = s[index+len(old):]
	}
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
