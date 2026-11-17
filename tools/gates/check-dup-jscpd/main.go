// Package dupjscpd is the compiled port of tools/check-dup-jscpd.py.
//
// The SECOND duplication detector: jscpd 5.4.1 (MIT, a self-contained Rust
// binary) over the maintained C# surface, compared with the committed
// baseline.
//
// The repository already has a duplication gate (check-test-dup, #1778), and
// this one does NOT replace it: it asks a different question with a different
// engine, and the two answers do not agree, which is the whole reason this
// gate exists.
//
//	check-test-dup  a duplicated TEST METHOD BODY, matched on the shape of
//	                the body. Scope: tests/**/*.cs.
//	THIS ONE        a duplicated BLOCK OF CODE, matched on the token stream
//	                of a window. Scope: src/, apps/, tests/ -- and it sees
//	                duplication that lives OUTSIDE a method body.
//
// THE RULES
//
//	DUP-FLOOR           The scan analysed fewer files than the floor. A gate
//	                    that measured nothing must not be green.
//	DUP-GROWTH          The tree's duplicated LINES are above the baseline's.
//	DUP-BASELINE-STALE  The tree's duplicated lines are BELOW the baseline's:
//	                    a warning, never a failure.
//	DUP-MEASUREMENT     The config or the tool version moved since the
//	                    baseline was written.
//	DUP-NEW-CLONES      The number of clones that are new relative to the base
//	                    ref: reported, blocking only with --fail-on-new-clones.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package dupjscpd

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The tool, its pinned assets and the files the gate reads.
const (
	toolName     = "jscpd"
	toolVersion  = "5.4.1"
	toolLicense  = "MIT"
	toolReleases = "https://github.com/kucherenko/jscpd/releases/download"

	configName   = ".jscpd.json"
	baselineName = "tools/jscpd-baseline.json"
	reportName   = "jscpd-report.json"
	cacheDir     = "tmp/jscpd"
)

// The pinned asset per platform, with the sha256 from the release's own
// checksums.txt. The tool is MIT-licensed; the binary is fetched, verified
// and thrown away with the CI job.
var toolAssets = map[string][2]string{
	"linux-x64": {
		"jscpd-linux-x64-gnu.tar.gz",
		"f2017a17de2a8d5708f8354c6e7223f82da3dc6489675bce543ba00d9d529468",
	},
	"linux-arm64": {
		"jscpd-linux-arm64-gnu.tar.gz",
		"6f8ddab0aec0232d08a6aa0c36cddb435ddde2c24a447cc9afe603ac8556748c",
	},
	"darwin-x64": {
		"jscpd-darwin-x64.tar.gz",
		"101a9b9e0d1a4ffea7b9ac86d91003745ef471f00a8a2a921353a65647b5d12f",
	},
	"darwin-arm64": {
		"jscpd-darwin-arm64.tar.gz",
		"48dedc0b8fa4f2900fc6ac8f06797e8eee7e36a8ab4f67f54b1ad25fd7ae103c",
	},
}

// Run is the gate entry point.
func Run(args []string) int {
	root := ""
	run := false
	download := false
	jscpd := ""
	report := reportName
	baseline := baselineName
	config := configName
	cache := cacheDir
	minFiles := 0
	failOnGrowth := true
	failOnNewClones := -1
	baselineRef := ""
	update := false
	selfTest := false

	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch {
		case arg == "--root":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--root needs a path")
				return gate.Broken
			}
			i++
			root = args[i]
		case arg == "--run":
			run = true
		case arg == "--download":
			download = true
		case arg == "--jscpd":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--jscpd needs a path")
				return gate.Broken
			}
			i++
			jscpd = args[i]
		case arg == "--report":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--report needs a path")
				return gate.Broken
			}
			i++
			report = args[i]
		case arg == "--baseline":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--baseline needs a path")
				return gate.Broken
			}
			i++
			baseline = args[i]
		case arg == "--config":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--config needs a path")
				return gate.Broken
			}
			i++
			config = args[i]
		case arg == "--cache-dir":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--cache-dir needs a path")
				return gate.Broken
			}
			i++
			cache = args[i]
		case arg == "--min-files":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			i++
			value, err := parseInt(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = value
		case arg == "--fail-on-growth":
			failOnGrowth = true
		case arg == "--no-fail-on-growth":
			failOnGrowth = false
		case arg == "--fail-on-new-clones":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--fail-on-new-clones needs a number")
				return gate.Broken
			}
			i++
			value, err := parseInt(args[i])
			if err != nil {
				fmt.Fprintln(os.Stderr, "--fail-on-new-clones needs a number")
				return gate.Broken
			}
			failOnNewClones = value
		case arg == "--baseline-ref":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--baseline-ref needs a ref")
				return gate.Broken
			}
			i++
			baselineRef = args[i]
		case arg == "--update":
			update = true
		case arg == "--self-test":
			selfTest = true
		case arg == "--help" || arg == "-h":
			fmt.Println("usage: harbor-gates check-dup-jscpd [--root PATH] [--run] [--download] [--jscpd PATH] [--report PATH] [--baseline PATH] [--config PATH] [--cache-dir PATH] [--min-files N] [--fail-on-growth|--no-fail-on-growth] [--fail-on-new-clones N] [--baseline-ref REF] [--update] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	repoRoot, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}
	if root != "" {
		repoRoot = root
	}

	if download {
		binary, err := downloadTool(filepath.Join(repoRoot, cache), true)
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		fmt.Printf("  %s %s at %s\n", toolName, toolVersion, binary)
		return gate.Green
	}

	reportPath := report
	if !filepath.IsAbs(reportPath) {
		reportPath = filepath.Join(repoRoot, reportPath)
	}
	baselinePath := baseline
	if !filepath.IsAbs(baselinePath) {
		baselinePath = filepath.Join(repoRoot, baselinePath)
	}
	configPath := config
	if !filepath.IsAbs(configPath) {
		configPath = filepath.Join(repoRoot, configPath)
	}

	if run {
		binary := jscpd
		if binary == "" {
			downloaded, err := downloadTool(filepath.Join(repoRoot, cache), false)
			if err != nil {
				fmt.Fprintln(os.Stderr, err.Error())
				return gate.Broken
			}
			binary = downloaded
		}
		var extra []string
		if baselineRef != "" {
			extra = []string{"--baseline-from-ref", baselineRef}
		}
		if err := runTool(binary, repoRoot, reportPath, configPath, extra); err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
	}

	if update {
		reportData, err := readReport(reportPath)
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		baselineData, err := buildBaseline(reportData, repoRoot)
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		if err := os.MkdirAll(filepath.Dir(baselinePath), 0o755); err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		text, err := baselineData.JSON()
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		if err := os.WriteFile(baselinePath, []byte(text), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Broken
		}
		fmt.Printf("  wrote %s\n", baselinePath)
		return gate.Green
	}

	reportData, err := readReport(reportPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	baselineData, err := readBaseline(baselinePath)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	if baselineData.empty {
		fmt.Printf("  no baseline at %s; nothing to compare against\n", baselinePath)
	}
	findings, warnings, summary := Analyse(reportData, baselineData, minFiles, failOnGrowth, failOnNewClones, configFingerprint(repoRoot))
	printSummary(summary)
	for _, warning := range warnings {
		fmt.Printf("  warn  %s\n", warning)
	}
	for _, finding := range findings {
		fmt.Printf("  FAIL  %s\n", finding)
	}
	if len(findings) > 0 {
		return gate.Red
	}
	fmt.Println("OK: no duplication beyond the committed baseline")
	return gate.Green
}

// parseInt reads an integer argument.
func parseInt(text string) (int, error) {
	value := 0
	if text == "" {
		return 0, fmt.Errorf("empty number")
	}
	for _, r := range text {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("not a number: %s", text)
		}
		value = value*10 + int(r-'0')
	}
	return value, nil
}

// relativeTo returns a path relative to the root, with '/' separators.
func relativeTo(root string, path string) string {
	relative, err := filepath.Rel(root, path)
	if err != nil {
		return filepath.ToSlash(path)
	}
	return filepath.ToSlash(relative)
}

// joinCommand renders a command the way the python original did: the
// arguments joined with a space.
func joinCommand(parts []string) string {
	return strings.Join(parts, " ")
}
