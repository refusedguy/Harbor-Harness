package shardbalance

// selftest.go -- the gate's own proof that it can fail.
//
// Eight fixtures, one per rule:
//
//   1. a clean tree stays clean, with the walls the gate computes;
//   2. a runaway shard (one project's cost blown up) fails with
//      SHARD-SKEW-EXCEEDED;
//   3. the same tree under a lax threshold is clean again — the rule is the
//      threshold, not a hard-coded number;
//   4. a project in two shards fails;
//   5. a recorded cost for a project no shard runs fails;
//   6. a missing measurement file is an ERROR, not a clean verdict;
//   7. a source with no shard list is an error too;
//   8. a project with no measured cost is a NOTE, not a finding.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"

	"harbor.local/gates/internal/gate"
)

type shardEntry struct {
	Projects []string `json:"projects"`
	CLI      bool     `json:"cli"`
}

type cleanSource struct {
	Version int                   `json:"version"`
	Shards  map[string]shardEntry `json:"shards"`
}

type cleanCosts struct {
	Projects map[string]float64 `json:"projects"`
	Overhead map[string]float64 `json:"overhead"`
}

func cleanSourceFixture() cleanSource {
	return cleanSource{
		Version: 1,
		Shards: map[string]shardEntry{
			"core":     {Projects: []string{"Alpha.Tests", "Beta.Tests"}, CLI: false},
			"platform": {Projects: []string{"Gamma.Tests"}, CLI: false},
		},
	}
}

func cleanCostsFixture() cleanCosts {
	return cleanCosts{
		Projects: map[string]float64{"Alpha.Tests": 10.0, "Beta.Tests": 10.0, "Gamma.Tests": 10.0},
		Overhead: map[string]float64{"perShardSeconds": 1.0, "perProjectSeconds": 0.5},
	}
}

// writeTree writes the source and the measurement into a throwaway tree.
func writeTree(root string, source cleanSource, costs cleanCosts) {
	if err := os.MkdirAll(filepath.Join(root, "tools"), 0o755); err != nil {
		return
	}
	writeJSON(filepath.Join(root, sourcePath), source)
	writeJSON(filepath.Join(root, costsPath), costs)
}

func writeJSON(path string, value any) {
	data, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return
	}
	os.WriteFile(path, data, 0o644)
}

// run is the gate's verdict for a tree, in the shape the self-test unpacks.
func run(root string, maxSkew float64) ([]finding, []string, []row, float64) {
	shards, err := readShards(filepath.Join(root, sourcePath))
	if err != "" {
		return []finding{{"SHARD-SOURCE-UNREADABLE", sourcePath, err}}, nil, nil, 0
	}
	costs, err := readCosts(filepath.Join(root, costsPath))
	if err != "" {
		return []finding{{"SHARD-COST-UNREADABLE", costsPath, err}}, nil, nil, 0
	}
	findings, notes, rows, skew, _, _ := analyse(shards, costs)
	if f := skewFinding(rows, skew, maxSkew); f != nil {
		findings = append(findings, *f)
	}
	return findings, notes, rows, skew
}

func codesOf(findings []finding) map[string]bool {
	out := map[string]bool{}
	for _, f := range findings {
		out[f.code] = true
	}
	return out
}

func selfTestRun() int {
	var failures []string
	check := func(name string, condition bool, detail string) {
		if !condition {
			failures = append(failures, fmt.Sprintf("%s: %s", name, detail))
		}
	}

	root, err := os.MkdirTemp("", "shard-balance-")
	if err != nil {
		fmt.Printf("self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	// 1. A clean tree is clean.
	writeTree(root, cleanSourceFixture(), cleanCostsFixture())
	findings, _, rows, skew := run(root, defaultMaxSkew)
	check("clean", len(findings) == 0, fmt.Sprintf("the clean tree produced %v", findings))
	check("clean-rows", len(rows) == 2, fmt.Sprintf("the clean tree produced %v", rows))
	check("clean-skew", skew < defaultMaxSkew, fmt.Sprintf("the clean tree skewed %v", skew))
	walls := map[string]float64{}
	for _, r := range rows {
		walls[r.shard] = round3(r.wall)
	}
	cleanWalls := map[string]float64{"core": 22.0, "platform": 11.5}
	same := len(walls) == len(cleanWalls)
	if same {
		for shard, wall := range cleanWalls {
			if walls[shard] != wall {
				same = false
			}
		}
	}
	check("clean-walls", same, fmt.Sprintf("the clean tree computed %v", walls))

	// 2. The runaway shard. One project costs ten times what the others do,
	//    and the gate must fail on the skew.
	runaway := cleanCostsFixture()
	runaway.Projects["Alpha.Tests"] = 400.0
	writeTree(root, cleanSourceFixture(), runaway)
	findings, _, _, _ = run(root, defaultMaxSkew)
	check("runaway", codesOf(findings)["SHARD-SKEW-EXCEEDED"],
		fmt.Sprintf("the runaway shard produced %v", codesOf(findings)))

	// 3. The same tree under a lax threshold is clean again.
	findings, _, _, _ = run(root, 1000.0)
	check("runaway-lax", len(findings) == 0,
		fmt.Sprintf("the runaway shard still failed under a lax threshold: %v", findings))

	// 4. A project in two shards.
	duplicated := cleanSourceFixture()
	platform := duplicated.Shards["platform"]
	platform.Projects = append(platform.Projects, "Alpha.Tests")
	duplicated.Shards["platform"] = platform
	writeTree(root, duplicated, cleanCostsFixture())
	findings, _, _, _ = run(root, defaultMaxSkew)
	check("duplicated", codesOf(findings)["SHARD-PROJECT-DUPLICATED"],
		fmt.Sprintf("the duplicated project produced %v", codesOf(findings)))

	// 5. A recorded cost for a project no shard runs.
	stale := cleanCostsFixture()
	stale.Projects["Gone.Tests"] = 1.0
	writeTree(root, cleanSourceFixture(), stale)
	findings, _, _, _ = run(root, defaultMaxSkew)
	check("stale", codesOf(findings)["SHARD-COST-STALE"],
		fmt.Sprintf("the stale cost produced %v", codesOf(findings)))

	// 6. A missing measurement file is an error, not a clean verdict.
	os.Remove(filepath.Join(root, costsPath))
	findings, _, _, _ = run(root, defaultMaxSkew)
	check("missing-costs", len(findings) > 0, "a missing measurement file produced a clean verdict")

	// 7. A source with no shard list is an error too.
	writeTree(root, cleanSource{Version: 1, Shards: map[string]shardEntry{}}, cleanCostsFixture())
	findings, _, _, _ = run(root, defaultMaxSkew)
	check("missing-shards", codesOf(findings)["SHARD-SOURCE-UNREADABLE"],
		fmt.Sprintf("a source with no shard list produced %v", codesOf(findings)))

	// 8. A project with no measured cost is a note, not a finding.
	unmeasured := cleanCostsFixture()
	delete(unmeasured.Projects, "Gamma.Tests")
	writeTree(root, cleanSourceFixture(), unmeasured)
	findings, notes, rows, _ := run(root, defaultMaxSkew)
	check("unmeasured", len(findings) == 0, fmt.Sprintf("an unmeasured project produced %v", findings))
	check("unmeasured-note", len(notes) > 0, "an unmeasured project produced no note")
	priced := false
	for _, r := range rows {
		if r.shard == "platform" && r.wall > 0 {
			priced = true
		}
	}
	check("unmeasured-priced", priced, fmt.Sprintf("the unmeasured project was not priced: %v", rows))

	if len(failures) > 0 {
		fmt.Println("self-test FAILED:")
		for _, failure := range failures {
			fmt.Printf("  - %s\n", failure)
		}
		return gate.Red
	}
	fmt.Println("ok: the self-test proves the gate fails on a runaway shard, on a " +
		"duplicated project and on a stale cost, and stays quiet on a " +
		"clean tree and on an unmeasured project")
	return gate.Green
}

// round3 rounds to three decimals, the way python's round(x, 3) does.
func round3(value float64) float64 {
	scaled := value * 1000
	if scaled < 0 {
		return float64(int64(scaled-0.5)) / 1000
	}
	return float64(int64(scaled+0.5)) / 1000
}

// sortedProjects is a small helper the self-test uses to print a stable
// list.
func sortedProjects(values []string) []string {
	out := make([]string, len(values))
	copy(out, values)
	sort.Strings(out)
	return out
}
