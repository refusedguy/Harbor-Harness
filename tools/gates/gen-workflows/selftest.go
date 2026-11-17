package genworkflows

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// selfTestRun proves that the checker can fail.
//
// A gate that cannot fail is indistinguishable from a gate that found
// nothing, and the whole claim of this gate is that it makes a hand edit of
// a generated workflow file visible. So the self-test writes a scratch tree,
// checks it (green), then for EVERY file in the registry changes one line by
// hand (red, named as that file) and removes the file (red, named as that
// file), and changes nothing at all (green again).
//
// Every file, not just the first one, matters. The registry is the list of
// files the gate claims to own, and a self-test that only proved the first
// entry would keep passing while every later entry stopped being checked.
// The finding has to name the file that was touched: a red check caused by
// one entry cannot stand in for proof about another.
//
// It also proves the property the check rests on: the generator is a
// function. The same model rendered twice has to be the same bytes, or the
// comparison is a comparison of two runs and not of two trees.
func selfTestRun() int {
	var failures []string

	// 1. The generator is a function: the same model renders to the same
	//    bytes, every time.
	for _, entry := range registry {
		first := entry.build().Render()
		second := entry.build().Render()
		if first != second {
			failures = append(failures, fmt.Sprintf(
				"%s: two renders of the same model differ (%d and %d bytes)",
				entry.path, len(first), len(second)))
		}
		if !strings.HasSuffix(first, "\n") {
			failures = append(failures, fmt.Sprintf(
				"%s: the rendered file does not end with a newline", entry.path))
		}
	}

	// 2. A tree the gate wrote is green.
	scratch, err := os.MkdirTemp("", "gen-workflows-selftest-")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	defer os.RemoveAll(scratch)

	for _, entry := range registry {
		path := filepath.Join(scratch, entry.path)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		if err := os.WriteFile(path, []byte(entry.build().Render()), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}
	if findings, _ := checkTree(scratch, 0); len(findings) > 0 {
		failures = append(failures, fmt.Sprintf(
			"a tree the gate just wrote is not green: %s", findings[0]))
	}

	// 3. One hand-edited line in EACH generated file is red, and the gate
	//    names the file that was edited. This is the claim of the gate.
	for _, entry := range registry {
		path := filepath.Join(scratch, entry.path)
		original, err := os.ReadFile(path)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		edited := strings.Replace(string(original), "runs-on:", "runs-on: ", 1)
		if edited == string(original) {
			edited = strings.Replace(string(original), "name:", "name: ", 1)
		}
		if edited == string(original) {
			failures = append(failures, fmt.Sprintf(
				"%s: could not plant a hand edit in the generated file", entry.path))
			continue
		}
		if err := os.WriteFile(path, []byte(edited), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		findings, _ := checkTree(scratch, 0)
		if !namesFile(findings, entry.path) {
			failures = append(failures, fmt.Sprintf(
				"%s: a hand edit of one line did not make the check name this file", entry.path))
		}
		if err := os.WriteFile(path, original, 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}

	// 4. Each generated file, when missing, makes the check red and names that file.
	for _, entry := range registry {
		path := filepath.Join(scratch, entry.path)
		if err := os.WriteFile(path, []byte(entry.build().Render()), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		if err := os.Remove(path); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
		findings, _ := checkTree(scratch, 0)
		if !namesFile(findings, entry.path) {
			failures = append(failures, fmt.Sprintf(
				"%s: a missing generated file did not make the check name this file", entry.path))
		}
		if err := os.WriteFile(path, []byte(entry.build().Render()), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}

	// 5. The floor holds: a registry with fewer files than the floor is red,
	//    because a check that read nothing would agree with itself.
	findings, _ := checkTree(scratch, len(registry)+1)
	if len(findings) == 0 {
		failures = append(failures, "a registry below the floor did not make the check red")
	}

	if len(failures) > 0 {
		fmt.Printf("Generated-workflow gate self-test — failures:\n\n")
		for _, failure := range failures {
			fmt.Printf("  %s\n", failure)
		}
		fmt.Print()
		return gate.Red
	}

	fmt.Printf("OK: the generator is deterministic, and for all %d registered file(s) the checker is red and names the file on a hand edit and on a missing file; it is red below its floor\n",
		len(registry))
	return gate.Green
}

// namesFile reports whether the findings include one about this exact file.
// A red check is not enough: with a registry of more than one file, a failure
// about a different file would let a broken entry pass its own self-test.
func namesFile(findings []finding, path string) bool {
	for _, item := range findings {
		if item.path == path {
			return true
		}
	}
	return false
}
