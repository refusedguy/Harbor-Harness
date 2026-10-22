// Package shardbalance is the compiled port of tools/check-shard-balance.py.
//
// It asks whether the CI test shards are balanced, judged by a RECORDED
// measurement of what every project cost. It measures nothing itself: a
// threshold on a live wall clock fails on a slow runner, and a gate that
// fails on a slow runner gets switched off.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package shardbalance

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

const (
	sourcePath = "tools/ci-project-lists.json"
	costsPath  = "tools/shard-costs.json"

	// The skew the gate allows: the ratio of the heaviest shard's wall to
	// the lightest one's.
	defaultMaxSkew = 4.0

	// A shard's wall is the sum of its projects' measured costs plus what
	// the shard spends on the projects themselves.
	defaultPerProjectSeconds = 0.6
	defaultPerShardSeconds   = 2.0
)

// finding is one thing the gate found.
type finding struct {
	code    string
	subject string
	message string
}

func (f finding) String() string {
	return fmt.Sprintf("%s  %s: %s", f.code, f.subject, f.message)
}

// row is one shard's line of the table the gate prints.
type row struct {
	shard    string
	projects int
	measured float64
	wall     float64
}

// Run is the gate entry point.
func Run(args []string) int {
	source := sourcePath
	costs := costsPath
	maxSkew := defaultMaxSkew
	minProjects := 0
	selfTest := false

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--source":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--source needs a path")
				return gate.Broken
			}
			i++
			source = args[i]
		case "--costs":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--costs needs a path")
				return gate.Broken
			}
			i++
			costs = args[i]
		case "--max-skew":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--max-skew needs a number")
				return gate.Broken
			}
			i++
			value, ok := parseFloat(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--max-skew needs a number")
				return gate.Broken
			}
			maxSkew = value
		case "--min-projects":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-projects needs a number")
				return gate.Broken
			}
			i++
			value, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-projects needs a number")
				return gate.Broken
			}
			minProjects = value
		case "--self-test":
			selfTest = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	if maxSkew <= 0 {
		fmt.Println("::error::--max-skew must be a positive number")
		return gate.Broken
	}

	shards, err := readShards(source)
	if err != "" {
		fmt.Printf("::error::%s\n", err)
		return gate.Broken
	}
	costsData, err := readCosts(costs)
	if err != "" {
		fmt.Printf("::error::%s\n", err)
		return gate.Broken
	}

	findings, notes, rows, skew, heaviest, lightest := analyse(shards, costsData)

	projectCount := 0
	for _, projects := range shards {
		projectCount += len(projects)
	}
	if minProjects > 0 && projectCount < minProjects {
		fmt.Printf("::error::the shards run %d project(s) between them, and the floor is %d. "+
			"A layout that reads fewer projects than that is a layout this gate cannot judge.\n",
			projectCount, minProjects)
		return gate.Red
	}

	fmt.Printf("shard balance, from the measurement of run %s (%s, %s):\n",
		costsData.sourceRun, costsData.sourceSha, costsData.sourceDate)
	fmt.Println("| shard | projects | measured s | wall s |")
	fmt.Println("|---|---:|---:|---:|")
	for _, r := range rows {
		fmt.Printf("| %s | %d | %.1f | %.1f |\n", r.shard, r.projects, r.measured, r.wall)
	}
	fmt.Printf("heaviest %.1f s, lightest %.1f s, skew %.2fx (the limit is %.2fx)\n",
		heaviest, lightest, skew, maxSkew)
	for _, note := range notes {
		fmt.Printf("note: %s\n", note)
	}

	if skew > maxSkew {
		if f := skewFinding(rows, skew, maxSkew); f != nil {
			findings = append(findings, *f)
		}
	}

	if len(findings) > 0 {
		fmt.Println("::error::the CI test shards are not balanced:")
		for _, f := range findings {
			fmt.Printf("  - %s\n", f.String())
		}
		return gate.Red
	}

	fmt.Println("ok: the shards are balanced by the recorded measurement")
	return gate.Green
}

// readShards reads the shard lists of the source.
//
// The error is a string when the file is missing, unreadable, or does not
// carry the shape this gate reads; the gate then reports the problem
// instead of a clean verdict.
func readShards(path string) (map[string][]string, string) {
	if !isFile(path) {
		return nil, fmt.Sprintf("the source %s does not exist", path)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Sprintf("the source %s could not be read: %v", path, err)
	}
	var document any
	if err := json.Unmarshal(data, &document); err != nil {
		return nil, fmt.Sprintf("the source %s could not be read: %v", path, err)
	}
	root, ok := document.(map[string]any)
	if !ok {
		return nil, fmt.Sprintf("the source %s is not an object", path)
	}
	rawShards, ok := root["shards"].(map[string]any)
	if !ok || len(rawShards) == 0 {
		return nil, fmt.Sprintf("the source %s carries no shards", path)
	}
	projects := map[string][]string{}
	for _, name := range sortedKeys(rawShards) {
		entry, ok := rawShards[name].(map[string]any)
		if !ok {
			return nil, fmt.Sprintf("the source %s gives the `%s` shard no project list", path, name)
		}
		list, ok := entry["projects"].([]any)
		if !ok {
			return nil, fmt.Sprintf("the source %s gives the `%s` shard no project list", path, name)
		}
		names := make([]string, 0, len(list))
		for _, item := range list {
			names = append(names, jsonString(item))
		}
		projects[name] = names
	}
	any := false
	for _, list := range projects {
		if len(list) > 0 {
			any = true
		}
	}
	if !any {
		return nil, fmt.Sprintf("the source %s names no project at all", path)
	}
	return projects, ""
}

// costData is the recorded measurement.
type costData struct {
	projects   map[string]float64
	overhead   map[string]any
	sourceRun  string
	sourceSha  string
	sourceDate string
}

// readCosts reads the recorded measurement: the per-project costs and the
// overhead model.
func readCosts(path string) (*costData, string) {
	if !isFile(path) {
		return nil, fmt.Sprintf("the measurement file %s does not exist", path)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Sprintf("the measurement file %s could not be read: %v", path, err)
	}
	var document any
	if err := json.Unmarshal(data, &document); err != nil {
		return nil, fmt.Sprintf("the measurement file %s could not be read: %v", path, err)
	}
	root, ok := document.(map[string]any)
	if !ok {
		return nil, fmt.Sprintf("the measurement file %s is not an object", path)
	}
	rawProjects, ok := root["projects"].(map[string]any)
	if !ok || len(rawProjects) == 0 {
		return nil, fmt.Sprintf("the measurement file %s carries no projects", path)
	}
	projects := map[string]float64{}
	for _, name := range sortedKeys(rawProjects) {
		seconds, ok := rawProjects[name].(float64)
		if !ok || seconds < 0 {
			return nil, fmt.Sprintf(
				"the measurement file %s prices %s as %s, which is not a number of seconds",
				path, name, jsonRepr(rawProjects[name]))
		}
		projects[name] = seconds
	}
	overhead := map[string]any{}
	if value, ok := root["overhead"].(map[string]any); ok {
		overhead = value
	}
	source := map[string]any{}
	if value, ok := root["source"].(map[string]any); ok {
		source = value
	}
	return &costData{
		projects: projects,
		overhead: overhead,
		// The run id is a NUMBER in the file and a string in the message,
		// so the fields are rendered the way python's f-string renders
		// whatever it finds, and not read as strings.
		sourceRun:  valueOr(source, "run", "?"),
		sourceSha:  valueOr(source, "sha", "?"),
		sourceDate: valueOr(source, "date", "?"),
	}, ""
}

// analyse is the analysis of a layout against a measurement.
func analyse(shards map[string][]string, costs *costData) ([]finding, []string, []row, float64, float64, float64) {
	var findings []finding
	var notes []string

	perProject := costs.projects
	perProjectSeconds := defaultPerProjectSeconds
	perShardSeconds := defaultPerShardSeconds
	if value, ok := costs.overhead["perProjectSeconds"].(float64); ok {
		perProjectSeconds = value
	}
	if value, ok := costs.overhead["perShardSeconds"].(float64); ok {
		perShardSeconds = value
	}

	// A project in two shards runs twice per CI run, and the balance below
	// would count its cost twice.
	owner := map[string][]string{}
	for _, shard := range sortedKeys(shards) {
		for _, project := range shards[shard] {
			owner[project] = append(owner[project], shard)
		}
	}
	for _, project := range sortedKeys(owner) {
		where := owner[project]
		if len(where) > 1 {
			findings = append(findings, finding{
				"SHARD-PROJECT-DUPLICATED", project,
				fmt.Sprintf("the project runs in %d shards (%s), so it runs that many times "+
					"per CI run and its cost is counted that many times. Every project "+
					"belongs to exactly one shard.", len(where), strings.Join(where, ", ")),
			})
		}
	}

	// A recorded cost for a project no shard runs: the measurement describes
	// a tree that is not the tree.
	running := map[string]bool{}
	for project := range owner {
		running[project] = true
	}
	for _, project := range sortedKeys(perProject) {
		if !running[project] {
			findings = append(findings, finding{
				"SHARD-COST-STALE", project,
				fmt.Sprintf("the measurement records a cost for %s, and no shard runs it any "+
					"more. The measurement describes a tree that is not this one; re-measure it.",
					project),
			})
		}
	}

	// A project with no recorded cost is priced at the median of the
	// measured ones, so the balance stays honest while it waits for its own
	// measurement.
	var unmeasured []string
	for project := range running {
		if _, ok := perProject[project]; !ok {
			unmeasured = append(unmeasured, project)
		}
	}
	sort.Strings(unmeasured)
	fallback := 0.0
	if len(perProject) > 0 {
		values := make([]float64, 0, len(perProject))
		for _, v := range perProject {
			values = append(values, v)
		}
		fallback = median(values)
	}
	if len(unmeasured) > 0 {
		notes = append(notes, fmt.Sprintf(
			"%d project(s) have no measured cost and are priced at the median (%.2f s): %s. "+
				"Measure them and refresh %s to replace the estimate with a number.",
			len(unmeasured), fallback, strings.Join(unmeasured, ", "), costsPath))
	}

	var rows []row
	for _, shard := range sortedKeys(shards) {
		projects := shards[shard]
		measured := 0.0
		for _, project := range projects {
			if value, ok := perProject[project]; ok {
				measured += value
			} else {
				measured += fallback
			}
		}
		wall := perShardSeconds + perProjectSeconds*float64(len(projects)) + measured
		rows = append(rows, row{shard, len(projects), measured, wall})
	}

	skew := 0.0
	heaviest := 0.0
	lightest := 0.0
	if len(rows) > 0 {
		heaviest = rows[0].wall
		lightest = rows[0].wall
		for _, r := range rows {
			if r.wall > heaviest {
				heaviest = r.wall
			}
			if r.wall < lightest {
				lightest = r.wall
			}
		}
		if lightest > 0 {
			skew = heaviest / lightest
		} else {
			skew = inf()
		}
	} else {
		skew = inf()
	}

	return findings, notes, rows, skew, heaviest, lightest
}

// skewFinding is the finding for a layout that is too far apart.
func skewFinding(rows []row, skew, maxSkew float64) *finding {
	if skew <= maxSkew || len(rows) == 0 {
		return nil
	}
	worst := rows[0]
	best := rows[0]
	for _, r := range rows {
		if r.wall > worst.wall {
			worst = r
		}
		if r.wall < best.wall {
			best = r
		}
	}
	return &finding{
		"SHARD-SKEW-EXCEEDED", worst.shard,
		fmt.Sprintf("the `%s` shard would spend %.1f s and the `%s` shard %.1f s, a skew of "+
			"%.2fx, and the limit is %.2fx. The pull request waits for the heaviest shard, so a "+
			"skew is a wall of waiting on nothing. Move a project, or split the heavy shard.",
			worst.shard, worst.wall, best.shard, best.wall, skew, maxSkew),
	}
}
