// Package guardtriage is the compiled port of tools/guard-triage.py.
//
// It classifies guard firings into defect / style / noise / unknown. It reads
// what a CI run already produced (a TRX file or a job log) and writes the
// classified firings as a machine-readable JSON report, a Markdown report and
// a table in the job summary. The class of each rule is a human decision in
// tools/guard-triage-classes.json; the two mechanical detectors can only ever
// produce `noise`, the class that says the rule told us nothing new.
//
// The text this prints and the flags it takes are the ones the python
// original had. Exit codes: 0 clean, 1 the input was unusable, 2 (gate.Broken)
// the gate could not run. The original's self-test failure code was also 2,
// which is why the compiled gate uses the binary's "could not run" code for
// it: every compiled gate reports a failed self-test as gate.Broken.
package guardtriage

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/gate"
)

type options struct {
	trx          []string
	logs         []string
	classes      string
	guardDir     string
	repoRoot     string
	out          string
	markdown     string
	summary      string
	projects     []string
	minGuardFile int
	runs         string
	quiet        bool
	selfTest     bool
	help         bool
}

func parseOptions(args []string) (options, error) {
	var opt options
	opt.classes = "tools/guard-triage-classes.json"
	opt.guardDir = "tests/Harbor.Architecture.Tests"
	opt.repoRoot = "."
	for i := 0; i < len(args); i++ {
		arg := args[i]
		name, inline, hasInline := strings.Cut(arg, "=")
		value := func() (string, error) {
			if hasInline {
				return inline, nil
			}
			if i+1 >= len(args) {
				return "", fmt.Errorf("%s needs a value", name)
			}
			i++
			return args[i], nil
		}
		switch name {
		case "--trx":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.trx = append(opt.trx, v)
		case "--log":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.logs = append(opt.logs, v)
		case "--classes":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.classes = v
		case "--guard-dir":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.guardDir = v
		case "--repo-root":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.repoRoot = v
		case "--out":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.out = v
		case "--markdown":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.markdown = v
		case "--summary":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.summary = v
		case "--project":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.projects = append(opt.projects, v)
		case "--min-guard-files":
			v, err := value()
			if err != nil {
				return opt, err
			}
			number, err := strconv.Atoi(v)
			if err != nil {
				return opt, fmt.Errorf("--min-guard-files needs a number")
			}
			opt.minGuardFile = number
		case "--runs":
			v, err := value()
			if err != nil {
				return opt, err
			}
			opt.runs = v
		case "--quiet":
			opt.quiet = true
		case "--self-test":
			opt.selfTest = true
		case "--help", "-h":
			opt.help = true
		default:
			return opt, fmt.Errorf("unknown argument: %s", arg)
		}
	}
	return opt, nil
}

// Run is the gate entry point.
func Run(args []string) int {
	opt, err := parseOptions(args)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}
	if opt.help {
		fmt.Println("usage: harbor-gates guard-triage [--trx PATH] [--log PATH] [--classes PATH] [--guard-dir PATH] [--repo-root PATH] [--out PATH] [--markdown PATH] [--summary PATH] [--project NAME] [--min-guard-files N] [--runs PATH] [--quiet] [--self-test]")
		return gate.Green
	}
	if opt.selfTest {
		return selfTestRun()
	}

	repoRoot, err := filepath.Abs(opt.repoRoot)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}

	registryPath := opt.classes
	if !filepath.IsAbs(registryPath) {
		registryPath = filepath.Join(repoRoot, filepath.FromSlash(registryPath))
	}
	if info, err := os.Stat(registryPath); err != nil || info.IsDir() {
		fmt.Fprintf(os.Stderr, "guard-triage: no registry at %s\n", registryPath)
		return gate.Red
	}
	reg, err := loadRegistry(registryPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
		return gate.Red
	}

	guardDir := opt.guardDir
	if !filepath.IsAbs(guardDir) {
		guardDir = filepath.Join(repoRoot, filepath.FromSlash(guardDir))
	}
	index := buildGuardIndex(guardDir, repoRoot)
	if opt.minGuardFile > 0 && index.files < opt.minGuardFile {
		fmt.Fprintf(os.Stderr, "guard-triage: indexed %d guard file(s) under %s, the floor is %d. A guard project that moved would otherwise make this tool report an empty tree, which reads as 'no guard fired'.\n",
			index.files, guardDir, opt.minGuardFile)
		return gate.Red
	}

	var firings []*firing
	var trxUsed []string
	for _, raw := range opt.trx {
		for _, path := range expandInputs(raw, "*.trx") {
			found := firingsFromTRX(path)
			if len(found) > 0 {
				trxUsed = append(trxUsed, path)
				firings = append(firings, found...)
			}
		}
	}
	var logsUsed []string
	for _, raw := range opt.logs {
		for _, path := range expandInputs(raw, "*.log") {
			found := firingsFromLog(path)
			if len(found) > 0 {
				logsUsed = append(logsUsed, path)
				firings = append(firings, found...)
			}
		}
	}

	if len(opt.projects) > 0 {
		wanted := map[string]bool{}
		for _, project := range opt.projects {
			wanted[project] = true
		}
		kept := firings[:0]
		for _, f := range firings {
			if wanted[f.project] {
				kept = append(kept, f)
			}
		}
		firings = kept
	}
	firings = dedupe(firings)

	runIndex, runCreated := map[string]*jnode{}, map[string]string{}
	if opt.runs != "" {
		runPath := opt.runs
		if !filepath.IsAbs(runPath) {
			runPath = filepath.Join(repoRoot, filepath.FromSlash(runPath))
		}
		data, err := os.ReadFile(runPath)
		if err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
			return gate.Broken
		}
		runIndex, runCreated, err = loadRuns(data)
		if err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
			return gate.Broken
		}
	}

	for _, f := range firings {
		if f.runID == "" {
			f.runID = guessRunID(f.origin)
		}
		if node, ok := runIndex[f.runID]; ok {
			f.run = node
		}
		clsName, method := f.rule, f.rule
		if dot := strings.Index(f.rule, "."); dot >= 0 {
			clsName = f.rule[:dot]
		}
		if dot := strings.LastIndex(f.rule, "."); dot >= 0 {
			method = f.rule[dot+1:]
		}
		if path, line := index.locate(clsName, method); path != "" {
			f.guardFile = path
			f.guardLine = line
		}
		classify(f, reg, index, reg.allowanceText(repoRoot, f.rule))
	}

	sort.SliceStable(firings, func(i, j int) bool {
		if firings[i].rule != firings[j].rule {
			return firings[i].rule < firings[j].rule
		}
		return firings[i].runID < firings[j].runID
	})
	dateSet := map[string]bool{}
	for _, f := range firings {
		dateSet[f.runID] = true
	}
	var dates []string
	for date := range dateSet {
		dates = append(dates, date)
	}
	sort.Strings(dates)
	var stamps []string
	for _, f := range firings {
		if created := runCreated[f.runID]; created != "" {
			stamps = append(stamps, created)
		}
	}
	sort.Strings(stamps)
	windowFrom, windowTo := "", ""
	if len(stamps) > 0 {
		windowFrom, windowTo = stamps[0], stamps[len(stamps)-1]
	}

	report := buildReport(firings, trxUsed, logsUsed, windowFrom, windowTo, len(dates))
	payload := report.reportJSON().render(0) + "\n"
	if opt.out != "" {
		if err := os.WriteFile(opt.out, []byte(payload), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
			return gate.Broken
		}
	} else {
		fmt.Print(payload)
	}
	if opt.markdown != "" {
		if err := os.WriteFile(opt.markdown, []byte(renderMarkdown(report)+"\n"), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
			return gate.Broken
		}
	}
	if opt.summary != "" {
		fh, err := os.OpenFile(opt.summary, os.O_APPEND|os.O_WRONLY|os.O_CREATE, 0o644)
		if err != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
			return gate.Broken
		}
		_, err = fh.WriteString(renderSummary(report) + "\n")
		closeErr := fh.Close()
		if err != nil || closeErr != nil {
			fmt.Fprintf(os.Stderr, "guard-triage: %s\n", err.Error())
			return gate.Broken
		}
	}
	if !opt.quiet && opt.out != "" {
		fmt.Fprintf(os.Stderr,
			"guard-triage: %d firings, %d rules, noise %d, unknown %d -> %s\n",
			report.total, len(report.rules), report.byClass.noise, report.byClass.unknown, opt.out)
	}
	return gate.Green
}

func nodeMember(node *jnode, key string) *jnode {
	if node == nil {
		return nil
	}
	for i, name := range node.keys {
		if name == key {
			return node.values[i]
		}
	}
	return nil
}

// loadRuns reads the optional --runs metadata twice: once into an ordered
// tree (so the report preserves its key order) and once generically to pull
// out created_at and the run id of a list-shaped file.
func loadRuns(data []byte) (map[string]*jnode, map[string]string, error) {
	ordered, err := parseOrderedJSON(data)
	if err != nil {
		return nil, nil, err
	}
	var generic any
	if err := json.Unmarshal(data, &generic); err != nil {
		return nil, nil, err
	}
	runs := map[string]*jnode{}
	created := map[string]string{}
	switch value := generic.(type) {
	case map[string]any:
		for key, entry := range value {
			if node := nodeMember(ordered, key); node != nil {
				runs[key] = node
			}
			if object, ok := entry.(map[string]any); ok {
				created[key] = createdAtOf(object)
			}
		}
	case []any:
		for _, entry := range value {
			object, ok := entry.(map[string]any)
			if !ok {
				continue
			}
			key := ""
			if value, ok := object["run_id"]; ok && value != nil {
				key = pyStringOf(value)
			} else if value, ok := object["id"]; ok && value != nil {
				key = pyStringOf(value)
			}
			if key == "" {
				continue
			}
			created[key] = createdAtOf(object)
		}
		// The ordered list's entries are parallel to the generic ones.
		if ordered != nil && ordered.isArray {
			for i, entry := range value {
				object, ok := entry.(map[string]any)
				if !ok || i >= len(ordered.values) {
					continue
				}
				key := ""
				if value, ok := object["run_id"]; ok && value != nil {
					key = pyStringOf(value)
				} else if value, ok := object["id"]; ok && value != nil {
					key = pyStringOf(value)
				}
				if key != "" {
					runs[key] = ordered.values[i]
				}
			}
		}
	}
	return runs, created, nil
}

func createdAtOf(object map[string]any) string {
	value, ok := object["created_at"]
	if !ok || value == nil {
		return ""
	}
	return pyStringOf(value)
}

func pyStringOf(value any) string {
	switch typed := value.(type) {
	case string:
		return typed
	case float64:
		return strconv.FormatFloat(typed, 'g', -1, 64)
	case bool:
		if typed {
			return "True"
		}
		return "False"
	default:
		return fmt.Sprint(typed)
	}
}
