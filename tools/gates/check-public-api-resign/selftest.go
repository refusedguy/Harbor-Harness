package checkpublicapiresign

// selftest.go -- the gate's own proof that it can fail.
//
// The load-bearing non-vacuity pair: a commit that changes a public
// declaration WITHOUT re-signing the baseline must fail naming it, and a
// commit that re-signs the baseline must pass. If either stops failing, the
// gate is decorative.
//
// The fixture is a THROWAWAY repository under the temporary directory, with
// its own local identity: it is a repository of its own, not the repository
// this gate is committed in, and nothing about it touches the identity of the
// checkout the gate runs in.

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// git runs a git command in the fixture repository.
func git(cwd string, args ...string) {
	full := append([]string{"-C", cwd}, args...)
	cmd := exec.Command("git", full...)
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	cmd.Stdout = nil
	cmd.Stderr = nil
	_ = cmd.Run()
}

// writeFixture writes a file of the fixture repository.
func writeFixture(path string, text string) {
	os.MkdirAll(filepath.Dir(path), 0o755)
	os.WriteFile(path, []byte(text), 0o644)
}

// fixture is a throwaway repository with one frozen assembly and a baseline.
func fixture(root string, withBaseline bool) string {
	git(root, "init", "-q", "-b", "main")
	git(root, "config", "user.email", "gate@example.invalid")
	git(root, "config", "user.name", "gate")
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/Widget.cs"),
		"namespace Harbor.Abstractions;\n"+
			"public sealed class Widget\n"+
			"{\n"+
			"    public int Size { get; }\n"+
			"}\n")
	if withBaseline {
		writeFixture(filepath.Join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
			"#nullable enable\nHarbor.Abstractions.Widget\n")
	}
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "base")
	return root
}

// selfTestRun runs the gate against known-broken fixtures.
func selfTestRun() int {
	ok := true
	check := func(name string, condition bool, detail string) {
		ok = ok && condition
		mark := "ok"
		if !condition {
			mark = "<< MISMATCH"
		}
		fmt.Printf("  [%s] %s %s\n", pad(name, 46), mark, detail)
	}

	tmp, err := os.MkdirTemp("", "public-api-resign-selftest")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(tmp)

	root := fixture(tmp, true)

	// A clean tree: nothing to report.
	findings, count, err := runGate(root, "HEAD~0", "HEAD", map[string]int{})
	if err != nil {
		fmt.Println("  SELF-TEST FAIL:", err)
		return gate.Red
	}
	check("clean tree is clean", len(findings) == 0 && count == 0,
		"findings="+strings.Join(findings, ", "))

	// A commit that changes a public declaration without re-signing.
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/Widget.cs"),
		"namespace Harbor.Abstractions;\n"+
			"public sealed class Widget\n"+
			"{\n"+
			"    public int Size { get; }\n"+
			"    public int Weight { get; }\n"+
			"}\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "add a member without re-signing")
	findings, _, _ = runGate(root, "HEAD~1", "HEAD", map[string]int{})
	check("unre-signed declaration fails, naming it", hasPrefix(findings, "RESIGN-002"),
		"findings="+strings.Join(findings, ", "))

	// The same change WITH the re-signing, in ONE commit: clean. The amend
	// is deliberate — the rule is that the declaration and the row that
	// records it are ONE diff.
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
		"#nullable enable\nHarbor.Abstractions.Widget\nHarbor.Abstractions.Widget.get_Size -> int\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "--amend", "-m", "add a member and re-sign it")
	findings, _, _ = runGate(root, "HEAD~1", "HEAD", map[string]int{})
	check("re-signed declaration passes", len(findings) == 0,
		"findings="+strings.Join(findings, ", "))

	// A baseline edit with no code: fails.
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
		"#nullable enable\nHarbor.Abstractions.Widget\nHarbor.Abstractions.Widget.get_Size -> int\nHarbor.Abstractions.Widget.get_Weight -> int\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "baseline edit with no code")
	findings, _, _ = runGate(root, "HEAD~1", "HEAD", map[string]int{})
	check("baseline edit without code fails, naming it", hasPrefix(findings, "RESIGN-001"),
		"findings="+strings.Join(findings, ", "))

	// A doc-comment edit is not a declaration change: exempt.
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/Widget.cs"),
		"namespace Harbor.Abstractions;\n"+
			"/// <summary>A widget.</summary>\n"+
			"public sealed class Widget\n"+
			"{\n"+
			"    /// <summary>The size.</summary>\n"+
			"    public int Size { get; }\n"+
			"    public int Weight { get; }\n"+
			"}\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "doc comment only")
	findings, _, _ = runGate(root, "HEAD~1", "HEAD", map[string]int{})
	check("doc-comment edit is exempt", len(findings) == 0,
		"findings="+strings.Join(findings, ", "))

	// Creating the baseline is exempt: there is nothing to re-sign yet.
	other, err := os.MkdirTemp("", "public-api-resign-fixture")
	if err != nil {
		fmt.Println("  SELF-TEST FAIL: cannot create a temporary directory:", err)
		return gate.Red
	}
	defer os.RemoveAll(other)
	fresh := fixture(other, false)
	writeFixture(filepath.Join(fresh, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
		"#nullable enable\nHarbor.Abstractions.Widget\n")
	git(fresh, "add", "-A")
	git(fresh, "commit", "-q", "-m", "create the baseline")
	findings, _, _ = runGate(fresh, "HEAD~1", "HEAD", map[string]int{})
	check("creating the baseline is exempt", len(findings) == 0,
		"findings="+strings.Join(findings, ", "))

	// The floor: a truncated baseline fails.
	findings, _, _ = runGate(root, "HEAD~0", "HEAD", map[string]int{"src/Harbor.Abstractions": 99})
	check("floor fires on a short baseline", hasPrefix(findings, "RESIGN-FLOOR"),
		"findings="+strings.Join(findings, ", "))

	// A baseline edit that only REMOVES a row is a correction, not a
	// re-signing: no declaration changed, so no code has to arrive with it.
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
		"#nullable enable\nHarbor.Abstractions.Widget\nHarbor.Abstractions.Widget.get_Size -> int\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "re-sign the baseline with the member")
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
		"#nullable enable\nHarbor.Abstractions.Widget\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "drop a row that named nothing")
	findings, _, _ = runGate(root, "HEAD~1", "HEAD", map[string]int{})
	check("a baseline edit that only removes a row needs no code",
		!hasPrefix(findings, "RESIGN-001"), "findings="+strings.Join(findings, ", "))

	// A row written twice is a build that stops with RS0025.
	writeFixture(filepath.Join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
		"#nullable enable\nHarbor.Abstractions.Widget\nHarbor.Abstractions.Widget\n")
	git(root, "add", "-A")
	git(root, "commit", "-q", "-m", "the same row twice")
	findings, _, _ = runGate(root, "HEAD~1", "HEAD", map[string]int{})
	check("a row written twice is reported", hasPrefix(findings, "RESIGN-DUP"),
		"findings="+strings.Join(findings, ", "))
	git(root, "reset", "-q", "--hard", "HEAD~1")

	// The base is printed as its first twelve characters. A python slice of
	// a SHORT string is the whole string, and this port took that as a Go
	// slice, which PANICS on a short one. The benchmark passes `--base
	// HEAD~1`, CI passes a SHA, and the gate crashed on the short one.
	check("a short base is printed whole, not cut",
		shortSHA("HEAD~1") == "HEAD~1" && shortSHA("0123456789abcdef0123") == "0123456789ab",
		"shortSHA(HEAD~1)="+shortSHA("HEAD~1"))

	if ok {
		fmt.Println("\nself-test PASS")
		return gate.Green
	}
	fmt.Println("\nself-test FAIL")
	return gate.Red
}

// hasPrefix is whether one finding starts with a prefix.
func hasPrefix(findings []string, prefix string) bool {
	for _, item := range findings {
		if strings.HasPrefix(item, prefix) {
			return true
		}
	}
	return false
}

// pad is the python str.ljust.
func pad(text string, width int) string {
	runes := []rune(text)
	if len(runes) >= width {
		return text
	}
	return text + strings.Repeat(" ", width-len(runes))
}
