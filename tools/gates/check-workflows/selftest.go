package checkworkflows

// selftest.go -- the gate's own proof that it can fail, and that it does not
// cry wolf.
//
// Every fixture below is driven through the SAME scanner the gate runs, so a
// green self-test means "the matcher fired on the broken shapes and stayed
// quiet on the rest". The controls that must NOT fire are the half that
// matters: a gate that reports every step twice would be switched off within
// a week, and a gate that is switched off protects nothing.

import (
	"fmt"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The shapes that must be reported.
//
// The first one is the real defect: an arm64 twin written as a second
// `if:`/`shell:`/`run:` inside the first step's mapping.
const duplicateIfInOneStep = `name: ci
jobs:
  test:
    steps:
      - name: Report test measurements
        if: always() && runner.arch == 'X64'
        shell: bash
        run: |
          tools/bin/harbor-gates-linux-x64 test-measurements "TestResults"

        if: always() && runner.arch == 'ARM64'
        shell: bash
        run: |
          tools/bin/harbor-gates-linux-arm64 test-measurements "TestResults"
`

// A second `run:` in one step, with no `if:` to give it away.
const duplicateRunInOneStep = `name: ci
jobs:
  build:
    steps:
      - name: Build
        run: dotnet build
        run: dotnet test
`

// A matrix with two `shard:` keys in one mapping.
const duplicateInAMatrix = `name: ci
jobs:
  test:
    strategy:
      matrix:
        shard: core
        shard: e2e
`

// The shapes that must NOT be reported.

// The same key in two DIFFERENT mappings.
const sameKeyInSiblingMappings = `name: ci
jobs:
  build:
    name: build
    steps:
      - name: Build
        run: dotnet build
  test:
    name: test
    steps:
      - name: Test
        run: dotnet test
`

// The same key in two nested mappings of one step.
const sameKeyInNestedMappings = `name: ci
jobs:
  test:
    steps:
      - name: Report
        env:
          FOO: 1
        with:
          FOO: 2
        run: echo "$FOO"
`

// A block scalar whose body looks like a mapping.
const blockScalarWithKeyLikeLines = `name: ci
jobs:
  test:
    steps:
      - name: Report
        run: |
          echo "if: this is not a key"
          echo "shell: neither is this"
          echo "run: and this is not a key either"
      - name: Another
        if: always()
        run: echo done
`

// A sequence of plain scalars.
const sequenceOfScalars = `name: ci
on:
  push:
    branches: [dev, master]
    paths-ignore: ['**.md', 'docs/**']
jobs:
  build:
    steps:
      - uses: actions/checkout@v7
`

// Two sequence items with the same text: a list is not a mapping, and two
// equal entries in a list are legal YAML.
const repeatedListEntries = `name: ci
jobs:
  build:
    steps:
      - uses: actions/checkout@v7
      - uses: actions/checkout@v7
`

// selfTestRun drives the fixtures and reports what the scanner did.
func selfTestRun() int {
	var failures []string
	ran := 0

	check := func(what string, ok bool, detail string) {
		ran++
		if !ok {
			failures = append(failures, what)
			fmt.Printf("  FAIL  %s\n", what)
			if detail != "" {
				fmt.Printf("        %s\n", detail)
			}
		}
	}

	count := func(body string) int {
		return len(scanWorkflow("fixture.yml", strings.Split(body, "\n")))
	}

	// 1. The real defect is reported.
	check("a step with two `if:` keys is reported",
		count(duplicateIfInOneStep) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateIfInOneStep, "\n"))))

	// 2. And the finding names the key and both lines.
	found := scanWorkflow("fixture.yml", strings.Split(duplicateIfInOneStep, "\n"))
	check("the finding names the duplicated key",
		len(found) == 1 && found[0].key == "if", shown(found))
	check("the finding names the line of the second declaration",
		len(found) == 1 && found[0].line == 11, shown(found))
	check("the finding names the line of the first declaration",
		len(found) == 1 && found[0].firstLine == 6, shown(found))

	// 3. A second `run:` in one step is the same defect.
	check("a step with two `run:` keys is reported",
		count(duplicateRunInOneStep) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateRunInOneStep, "\n"))))

	// 4. A duplicate in a matrix is the same defect.
	check("a matrix with two `shard:` keys is reported",
		count(duplicateInAMatrix) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateInAMatrix, "\n"))))

	// 5. The same key in two different mappings is legal YAML.
	check("the same key in two sibling mappings is not reported",
		count(sameKeyInSiblingMappings) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(sameKeyInSiblingMappings, "\n"))))

	// 6. The same key in two nested mappings of one step is legal YAML.
	check("the same key in two nested mappings is not reported",
		count(sameKeyInNestedMappings) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(sameKeyInNestedMappings, "\n"))))

	// 7. A block scalar's body is content, not structure.
	check("a block scalar that looks like a mapping is not reported",
		count(blockScalarWithKeyLikeLines) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(blockScalarWithKeyLikeLines, "\n"))))

	// 8. A sequence of plain scalars is not a mapping.
	check("a sequence of plain scalars is not reported",
		count(sequenceOfScalars) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(sequenceOfScalars, "\n"))))

	// 9. Two equal entries in a list are legal YAML.
	check("two equal entries in a list are not reported",
		count(repeatedListEntries) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(repeatedListEntries, "\n"))))

	// 10. A clean file stays clean.
	check("a clean workflow file stays green",
		count(sequenceOfScalars) == 0, "")

	if len(failures) > 0 {
		fmt.Printf("\nself-test FAILED: %d of %d fixture(s): %s\n",
			len(failures), ran, strings.Join(failures, "; "))
		return gate.Red
	}
	fmt.Printf("\nself-test passed: %d fixtures\n", ran)
	return gate.Green
}

// shown is the detail of a failed fixture.
func shown(found []finding) string {
	if len(found) == 0 {
		return "(nothing was reported)"
	}
	parts := make([]string, 0, len(found))
	for _, item := range found {
		parts = append(parts, item.String())
	}
	return strings.Join(parts, "; ")
}
