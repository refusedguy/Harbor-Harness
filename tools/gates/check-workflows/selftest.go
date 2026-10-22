package checkworkflows

// selftest.go -- the gate's own proof that it can fail, and that it does not
// cry wolf.
//
// Every fixture below is driven through the SAME scanner the gate runs, so a
// green self-test means "the matcher fired on the broken shapes and stayed
// quiet on the rest". The controls that must NOT fire are the half that
// matters: a gate that reports every step twice would be switched off within
// a week, and a gate that is switched off protects nothing.
//
// The broken shapes are the three that make a file GitHub will not read: a
// mapping that declares a key twice (in the block form, in the quoted form
// and in the flow form), and a line that stands where a list item must
// stand.

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

// A duplicate in a NESTED map: the same defect one level deeper.
const duplicateInANestedMap = `name: ci
jobs:
  test:
    steps:
      - name: Call
        with:
          a: 1
          a: 2
`

// A duplicate behind a QUOTED key: the same defect with another spelling of
// the key.
const duplicateBehindQuotedKeys = `name: ci
jobs:
  test:
    steps:
      - name: Report
        "if": always()
        "if": ${{ false }}
`

// A duplicate inside a FLOW mapping: `with: {a: 1, a: 2}` is a mapping, and
// YAML forbids a duplicate key in it exactly as it forbids one in a block
// mapping.
const duplicateInAFlowMapping = `name: ci
jobs:
  test:
    steps:
      - name: Call
        with: {a: 1, a: 2}
`

// A step-shaped block that is NOT a list item: the arm64 twin written at the
// indent of the list instead of at the indent of the step. YAML reads it as a
// mapping where the file says there must be a list, and will not read the
// file at all.
const stepThatIsNotAListItem = `name: ci
jobs:
  test:
    steps:
      - name: Report
        if: always() && runner.arch == 'X64'
        run: |
          echo one

      if: always() && runner.arch == 'ARM64'
        run: |
          echo two
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

// A COMPACT sequence: the list sits at the indent of the key that owns it,
// and a key at that indent afterwards is a SIBLING of the key. A reader that
// cannot tell a compact sequence from an indented one reports this file as
// broken, and every workflow in the repository would be red.
const compactSequenceThenSiblingKey = `name: ci
compact:
- one
- two
after:
  ok: yes
jobs:
  build:
    runs-on: ubuntu-latest
`

// A flow mapping with distinct keys, and an empty one.
const flowMappingWithDistinctKeys = `name: ci
permissions: {}
jobs:
  build:
    steps:
      - name: Call
        with: {a: 1, b: 2}
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

	keys := func(body string) []string {
		found := scanWorkflow("fixture.yml", strings.Split(body, "\n"))
		out := make([]string, 0, len(found))
		for _, item := range found {
			out = append(out, item.key)
		}
		return out
	}

	// 1. The real defect is reported -- every key it declares twice, so a
	// reader sees the whole step and not only its first tell.
	found := scanWorkflow("fixture.yml", strings.Split(duplicateIfInOneStep, "\n"))
	check("a step with two `if:` keys is reported",
		count(duplicateIfInOneStep) == 3,
		shown(found))
	check("the findings name the duplicated keys",
		len(found) == 3 && found[0].key == "if" && found[1].key == "shell" && found[2].key == "run",
		shown(found))

	// 2. And the finding names the key and both lines.
	check("the finding names the duplicated key",
		len(found) == 3 && found[0].key == "if", shown(found))
	check("the finding names the line of the second declaration",
		len(found) == 3 && found[0].line == 11, shown(found))
	check("the finding names the line of the first declaration",
		len(found) == 3 && found[0].firstLine == 6, shown(found))

	// 3. A second `run:` in one step is the same defect.
	check("a step with two `run:` keys is reported",
		count(duplicateRunInOneStep) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateRunInOneStep, "\n"))))

	// 4. A duplicate in a matrix is the same defect.
	check("a matrix with two `shard:` keys is reported",
		count(duplicateInAMatrix) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateInAMatrix, "\n"))))

	// 5. A duplicate in a NESTED map is the same defect.
	check("a nested map with two `a:` keys is reported",
		count(duplicateInANestedMap) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateInANestedMap, "\n"))))

	// 6. A duplicate behind a quoted key is the same defect.
	check("a quoted key declared twice is reported",
		count(duplicateBehindQuotedKeys) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateBehindQuotedKeys, "\n"))))

	// 7. A duplicate inside a flow mapping is the same defect.
	check("a flow mapping with two `a:` keys is reported",
		count(duplicateInAFlowMapping) == 1,
		shown(scanWorkflow("fixture.yml", strings.Split(duplicateInAFlowMapping, "\n"))))

	// 8. A step-shaped block that is not a list item is a file GitHub will
	// not read.
	structural := scanWorkflow("fixture.yml", strings.Split(stepThatIsNotAListItem, "\n"))
	check("a step that is not a list item is reported",
		len(structural) == 1 && structural[0].kind == kindStructure,
		shown(structural))

	// 9. The same key in two different mappings is legal YAML.
	check("the same key in two sibling mappings is not reported",
		count(sameKeyInSiblingMappings) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(sameKeyInSiblingMappings, "\n"))))

	// 10. The same key in two nested mappings of one step is legal YAML.
	check("the same key in two nested mappings is not reported",
		count(sameKeyInNestedMappings) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(sameKeyInNestedMappings, "\n"))))

	// 11. A block scalar's body is content, not structure.
	check("a block scalar that looks like a mapping is not reported",
		count(blockScalarWithKeyLikeLines) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(blockScalarWithKeyLikeLines, "\n"))))

	// 12. A sequence of plain scalars is not a mapping.
	check("a sequence of plain scalars is not reported",
		count(sequenceOfScalars) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(sequenceOfScalars, "\n"))))

	// 13. Two equal entries in a list are legal YAML.
	check("two equal entries in a list are not reported",
		count(repeatedListEntries) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(repeatedListEntries, "\n"))))

	// 14. A compact sequence is legal YAML, and a reader that cannot tell it
	// from an indented one would report every workflow in the repository.
	check("a compact sequence followed by a sibling key is not reported",
		count(compactSequenceThenSiblingKey) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(compactSequenceThenSiblingKey, "\n"))))

	// 15. A flow mapping with distinct keys is legal YAML.
	check("a flow mapping with distinct keys is not reported",
		count(flowMappingWithDistinctKeys) == 0,
		shown(scanWorkflow("fixture.yml", strings.Split(flowMappingWithDistinctKeys, "\n"))))

	// 16. A clean file stays clean.
	check("a clean workflow file stays green",
		count(sequenceOfScalars) == 0, "")

	// 17. The keys the gate reports are the keys the file declares twice --
	// no more, no fewer.
	check("the reported keys are the duplicated ones",
		len(keys(duplicateInANestedMap)) == 1 && keys(duplicateInANestedMap)[0] == "a", "")

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
