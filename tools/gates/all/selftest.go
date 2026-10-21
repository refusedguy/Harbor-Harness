package all

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"harbor.local/gates/internal/gate"
)

// The self-test proves the failure paths of this command.
//
// Every case below is a shape that made an earlier version of this runner
// wrong, and two of them are here because they FAILED against a real draft:
//
//   missing_gate   the first draft skipped a gate it could not find. A gate
//                  that vanishes from a run is a gate that stops checking
//                  anything, and the run said nothing about it.
//   order          the first draft printed the rows in the order the gates
//                  finished in. Two runs of two machines then produced two
//                  reports, and nobody could tell whether the tree changed
//                  or the machine did.
//
// The fixtures are synthetic: a tree with an empty package directory per
// gate, and a runner that answers with what the case needs. The cases that
// read the REAL tree are the ones about the list itself — the list is a
// claim about the workflows, and the claim is checked against them.

// fakeRunner answers with what the case tells it to.
type fakeRunner struct {
	mu    sync.Mutex
	codes map[string]int
	order []string
	delay map[string]time.Duration
}

func (f *fakeRunner) run(g treeGate, args []string) (int, string, string, error) {
	f.mu.Lock()
	f.order = append(f.order, g.name)
	code := f.codes[g.name]
	delay := f.delay[g.name]
	f.mu.Unlock()
	if delay > 0 {
		time.Sleep(delay)
	}
	switch code {
	case gate.Green:
		return gate.Green, "OK: nothing to report\n", "", nil
	case gate.Red:
		return gate.Red, "a finding, on the first line\n", "", nil
	case gate.Broken:
		return gate.Broken, "", "the gate could not start\n", nil
	}
	return gate.Green, "OK: nothing to report\n", "", nil
}

// syntheticTree builds a tree where every gate of the list is compiled.
func syntheticTree(t *harness) string {
	root, err := os.MkdirTemp("", "gates-all-selftest")
	if err != nil {
		t.fail("cannot create the fixture tree: %v", err)
		return ""
	}
	for _, g := range treeGates {
		dir := filepath.Join(root, "tools", "gates", g.name)
		if err := os.MkdirAll(dir, 0o755); err != nil {
			t.fail("cannot create %s: %v", dir, err)
			return ""
		}
	}
	return root
}

// harness is the little result collector of the self-test.
//
// The self-test is not a test framework: it is a fixture harness that
// prints what it checked, so that the log of a CI run says which shape was
// proved and which was not.
type harness struct {
	cases int
	ok    bool
}

func (t *harness) fail(format string, args ...any) {
	t.ok = false
	fmt.Printf("  [%-52s] << %s\n", fmt.Sprintf(format, args...), "BROKEN")
}

func (t *harness) pass(name string) {
	t.cases++
	fmt.Printf("  [%-52s] ok\n", name)
}

// check runs one case and prints its verdict.
func (t *harness) check(name string, ok bool, detail string) {
	t.cases++
	if ok {
		fmt.Printf("  [%-52s] ok\n", name)
		return
	}
	t.ok = false
	fmt.Printf("  [%-52s] << %s\n", name, detail)
}

// selfTestRun is the self-test of the `all` command.
func selfTestRun() int {
	t := &harness{ok: true}

	// 1. The list is sorted and free of duplicates.
	sorted := true
	seen := map[string]bool{}
	previous := ""
	for _, g := range treeGates {
		if seen[g.name] {
			sorted = false
		}
		seen[g.name] = true
		if previous != "" && g.name < previous {
			sorted = false
		}
		previous = g.name
	}
	t.check("the list is sorted and free of duplicates", sorted,
		"the report's order is the list's order, and a list that is not sorted makes two runs differ for no reason")

	// 2. The list is honest: every gate of it is a gate a workflow runs,
	// and no workflow runs it with an artifact of the build on the command
	// line.
	root, err := gate.RepoRoot()
	if err != nil {
		t.fail("cannot determine the repository root: %v", err)
	} else {
		findings := checkList(root)
		detail := "the list names a gate that needs what the build produces, or a gate no workflow runs"
		if len(findings) > 0 {
			detail = findings[0]
		}
		t.check("the list names only gates that need the tree alone", len(findings) == 0, detail)
	}

	// 2b. The honesty check can FAIL. A check that cannot fire is
	// indistinguishable from a check that found nothing, and the shape it
	// must catch is a gate that reads what the build produces.
	planted := []invocation{
		{workflow: "ci.yml", job: "test", gate: "check-test-dup", args: "TestResults/core"},
	}
	found := checkInvocations(root, []treeGate{{name: "check-test-dup"}}, planted)
	t.check("a gate that reads a TRX is not a tree gate", len(found) == 1,
		"the check must fire on a gate that a workflow runs with an artifact of the build")
	unused := checkInvocations(root, []treeGate{{name: "check-nothing-runs-it"}}, nil)
	t.check("a gate no workflow runs is not in the list", len(unused) == 1,
		"a gate nobody runs is a gate nobody reviews")

	// 3. The synthetic tree: every gate of the list is compiled.
	fixture := syntheticTree(t)
	if fixture == "" {
		fmt.Println("\nself-test FAIL")
		return gate.Red
	}
	defer os.RemoveAll(fixture)

	// 3b. The runner RUNS the gates of the list that are compiled into the
	// binary, so a gate that no workflow calls directly is not a gate
	// nobody runs. Without this, the check would call a gate that the
	// `tree` job runs on every pull request a gate nobody runs, and would
	// block a pull request that changed nothing.
	runnerInvoked := []invocation{
		{workflow: "docs.yml", job: "tree", gate: runnerGate},
	}
	covered := checkInvocations(fixture, []treeGate{{name: treeGates[0].name}}, runnerInvoked)
	t.check("a gate the tree runner runs is not a gate nobody runs", len(covered) == 0,
		"the runner runs the gates of the list that are compiled into the binary")

	// 3c. And the runner does NOT run a gate that is still python: the
	// binary must not need python, and such a gate needs a workflow of its
	// own.
	pythonGate := treeGate{name: "still-python"}
	if err := os.WriteFile(filepath.Join(fixture, "tools", "still-python.py"),
		[]byte("# a gate that is still python\n"), 0o644); err != nil {
		t.fail("cannot create the python gate of the fixture: %v", err)
	}
	notCovered := checkInvocations(fixture, []treeGate{pythonGate}, runnerInvoked)
	t.check("a gate that is still python is not run by the runner", len(notCovered) == 1,
		"the binary must not need python: such a gate needs a workflow of its own")

	opts := options{workers: 4, timeout: defaultTimeout}

	// 4. Every gate green: the run is green.
	runner := &fakeRunner{codes: map[string]int{}}
	var out bytes.Buffer
	code := runAll(&out, fixture, "HEAD", opts, runner)
	t.check("a run where every gate is green exits 0", code == gate.Green,
		fmt.Sprintf("the run exited %d", code))

	// 5. One gate red: the run is red, and it names the gate.
	red := treeGates[0].name
	runner = &fakeRunner{codes: map[string]int{red: gate.Red}}
	out.Reset()
	code = runAll(&out, fixture, "HEAD", opts, runner)
	named := strings.Contains(out.String(), red)
	t.check("a run where one gate is red exits 1", code == gate.Red,
		fmt.Sprintf("the run exited %d", code))
	t.check("a red gate is named in the report", named,
		"a reader has to be able to see which gate found something")

	// 6. One gate that could not run: the run is broken, not red.
	broken := ""
	for _, g := range treeGates {
		if !g.writer {
			broken = g.name
			break
		}
	}
	runner = &fakeRunner{codes: map[string]int{broken: gate.Broken}}
	out.Reset()
	code = runAll(&out, fixture, "HEAD", opts, runner)
	t.check("a run where one gate could not run exits 2", code == gate.Broken,
		fmt.Sprintf("the run exited %d", code))

	// 7. A gate that is in neither tools/gates/ nor tools/: the run is
	// broken, and it says which gate is missing.
	missing := treeGates[1].name
	if err := os.RemoveAll(filepath.Join(fixture, "tools", "gates", missing)); err != nil {
		t.fail("cannot remove the fixture package: %v", err)
	}
	runner = &fakeRunner{codes: map[string]int{}}
	out.Reset()
	code = runAll(&out, fixture, "HEAD", opts, runner)
	text := out.String()
	t.check("a gate that exists in neither form exits 2", code == gate.Broken,
		fmt.Sprintf("the run exited %d", code))
	t.check("the missing gate is named", strings.Contains(text, missing),
		"a gate that cannot run must be named, not skipped")

	// 8. The order of the rows does not depend on the order the gates
	// finished in.
	restore := filepath.Join(fixture, "tools", "gates", missing)
	if err := os.MkdirAll(restore, 0o755); err != nil {
		t.fail("cannot restore the fixture package: %v", err)
	}
	delays := map[string]time.Duration{}
	for i, g := range treeGates {
		delays[g.name] = time.Duration(len(treeGates)-i) * time.Millisecond
	}
	runner = &fakeRunner{codes: map[string]int{}, delay: delays}
	out.Reset()
	_ = runAll(&out, fixture, "HEAD", opts, runner)
	first := rowOrder(out.String())
	out.Reset()
	runner = &fakeRunner{codes: map[string]int{}}
	_ = runAll(&out, fixture, "HEAD", opts, runner)
	second := rowOrder(out.String())
	t.check("two runs print the rows in the same order",
		strings.Join(first, ",") == strings.Join(second, ","),
		"the order of the report must not depend on the speed of the machine")
	t.check("the rows are sorted by name", isSorted(first),
		"the report's order is the list's order")

	fmt.Printf("\nfixtures: %d\n", t.cases)
	if !t.ok {
		fmt.Println("self-test FAIL")
		return gate.Red
	}
	fmt.Println("self-test PASS")
	return gate.Green
}

// rowOrder returns the gate names of the report's rows, in the order the
// report printed them.
func rowOrder(text string) []string {
	var names []string
	for _, line := range strings.Split(text, "\n") {
		if !strings.HasPrefix(line, "  ") || strings.Contains(line, "  code ") {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 3 {
			continue
		}
		// The rows are "<name>  <code>  <time>  <first line>"; the
		// not-run list is "<name>  <state>  <reason>" and is skipped by
		// the shape of the code column.
		if !isCode(fields[1]) {
			continue
		}
		names = append(names, fields[0])
	}
	return names
}

// isCode says whether a field is an exit code.
func isCode(field string) bool {
	switch field {
	case "0", "1", "2":
		return true
	}
	return false
}

// isSorted says whether the names are sorted.
func isSorted(names []string) bool {
	for i := 1; i < len(names); i++ {
		if names[i] < names[i-1] {
			return false
		}
	}
	return true
}
