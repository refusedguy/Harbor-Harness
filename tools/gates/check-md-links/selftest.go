package mdlinks

// selftest.go -- the gate's own proof that it can fail.
//
// The failure this guards against is a checker that has quietly stopped
// finding anything: it would still be green on every pull request, and the
// broken-link report above would be a lie. Each case below asserts a
// specific diagnostic, so "returns 0" alone is not enough to pass.
//
// The python original built a throwaway git repo per case and ran the gate
// as a process. The port calls the same functions the gate calls, on the
// same documents, so the fixture is the same one without the git plumbing.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/mdgate"
)

const (
	fixtureOK = "# Fixture\n\n" +
		"A real link to [target](target.md) and an external [one](https://example.com).\n\n" +
		"```\n" +
		"[not a link](gone.md)\n" +
		"```\n"
	fixtureTarget = "# Target\n\n## Real Anchor\n"
)

// writeFixture writes the documents into a throwaway tree and returns its
// root.
func writeFixture(files map[string]string) string {
	root, err := os.MkdirTemp("", "harbor-mdlinks-selftest")
	if err != nil {
		return ""
	}
	for rel, content := range files {
		full := filepath.Join(root, rel)
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			return ""
		}
		if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
			return ""
		}
	}
	return root
}

// scan runs the gate's scan over one document and returns the findings.
func scan(root string, rel string) (missing []string, anchors []string, total int) {
	raw, err := gate.ReadText(filepath.Join(root, rel))
	if err != nil {
		return nil, nil, 0
	}
	text := stripCode(raw)
	base := ""
	if dir := filepath.Dir(rel); dir != "." {
		base = dir
	}
	for _, h := range collectHits(text) {
		total++
		ok, kind, frag := resolve(root, base, h.target)
		line := rawLine(raw, text, h.offset)
		if !ok {
			missing = append(missing, fmt.Sprintf("L%d: %s", line, h.target))
			continue
		}
		if frag == "" || (kind != "file" && kind != "self") {
			continue
		}
		dest := normPath(joinPath(base, targetPath(h.target)))
		if strings.HasSuffix(dest, ".md") && exists(root, dest) {
			if !containsFold(headingAnchors(root, dest), strings.ToLower(frag)) {
				anchors = append(anchors, fmt.Sprintf("L%d: %s", line, h.target))
			}
		}
	}
	return missing, anchors, total
}

func selfTestRun() int {
	ok := true
	checks := 0
	expect := func(name string, good bool, detail string) {
		checks++
		if good {
			fmt.Printf("  ok   %s\n", name)
			return
		}
		ok = false
		fmt.Printf("  FAIL %s%s\n", name, detail)
	}

	// 1. A clean fixture is clean — otherwise every other case is
	// meaningless.
	root := writeFixture(map[string]string{
		"docs/ok.md":     fixtureOK,
		"docs/target.md": fixtureTarget,
	})
	defer os.RemoveAll(root)
	missing, anchors, total := scan(root, "docs/ok.md")
	expect("a clean fixture has no finding",
		len(missing) == 0 && len(anchors) == 0 && total == 2,
		fmt.Sprintf(" missing=%v anchors=%v total=%d", missing, anchors, total))

	// 2. A link to a file that is not there must fail, and must name the
	// target.
	root2 := writeFixture(map[string]string{
		"docs/ok.md":     fixtureOK + "\nBroken: [gone](nope/missing.md)\n",
		"docs/target.md": fixtureTarget,
	})
	defer os.RemoveAll(root2)
	missing, _, _ = scan(root2, "docs/ok.md")
	expect("a missing target is named",
		len(missing) == 1 && strings.Contains(missing[0], "nope/missing.md"),
		fmt.Sprintf(" missing=%v", missing))
	expect("a fenced code sample is not a link",
		!strings.Contains(strings.Join(missing, " "), "not a link"),
		fmt.Sprintf(" missing=%v", missing))

	// 3. An existing file with a heading that does not exist must fail.
	root3 := writeFixture(map[string]string{
		"docs/ok.md":     fixtureOK + "\nBad anchor: [target](target.md#no-such-anchor)\n",
		"docs/target.md": fixtureTarget,
	})
	defer os.RemoveAll(root3)
	missing, anchors, _ = scan(root3, "docs/ok.md")
	expect("a missing anchor is reported as an anchor",
		len(missing) == 0 && len(anchors) == 1 &&
			strings.Contains(anchors[0], "no-such-anchor"),
		fmt.Sprintf(" missing=%v anchors=%v", missing, anchors))

	// 4. The anti-vacuity floor: a real-but-tiny scan must NOT pass
	// silently.
	guard := mdgate.RequireNonVacuous("check-md-links", 2, 2, "link refs", 250, 500)
	expect("the floor below the file count fires", len(guard) > 0 &&
		strings.Contains(guard[0], "floor is 250"),
		fmt.Sprintf(" guard=%v", guard))

	// 5. A repo with no tracked markdown at all is a blind gate, not a pass.
	guard = mdgate.RequireNonVacuous("check-md-links", 0, 0, "link refs", 250, 500)
	expect("zero markdown files is a guard problem", len(guard) == 1 &&
		strings.Contains(guard[0], "scanned ZERO markdown files"),
		fmt.Sprintf(" guard=%v", guard))

	// 6. The GitHub slug algorithm: a leading emoji leaves a leading dash,
	// and a duplicate heading gets a -1 suffix.
	root4 := writeFixture(map[string]string{
		"docs/slug.md": "# 📊 Metrics\n\n# 📊 Metrics\n\n## Real Anchor\n",
	})
	defer os.RemoveAll(root4)
	anchorsSet := headingAnchors(root4, "docs/slug.md")
	expect("the emoji heading slugs to -metrics and -metrics-1",
		anchorsSet["-metrics"] && anchorsSet["-metrics-1"],
		fmt.Sprintf(" anchors=%v", anchorsSet))
	expect("a plain heading slugs to its text",
		anchorsSet["real-anchor"],
		fmt.Sprintf(" anchors=%v", anchorsSet))

	// 7. A non-ASCII heading keeps its letters: python's `\w` is unicode,
	// and Go's is not.
	root5 := writeFixture(map[string]string{"docs/ru.md": "# Привет\n"})
	defer os.RemoveAll(root5)
	anchorsSet = headingAnchors(root5, "docs/ru.md")
	expect("a non-ASCII heading keeps its letters",
		anchorsSet["привет"],
		fmt.Sprintf(" anchors=%v", anchorsSet))

	if ok {
		fmt.Printf("\nOK: check-md-links self-test — %d checks passed.\n", checks)
		return gate.Green
	}
	fmt.Println("\nFAIL: check-md-links self-test — a check failed.")
	return gate.Red
}
