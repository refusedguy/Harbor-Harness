// selftest.go -- the gate's own proof that it can fail.
//
// A gate that never goes red is a gate that proves nothing, and the failure
// mode it guards is a GREEN build on a tree that does not compile. The
// fixtures below are the shapes that matter:
//
//   * the SKIPPED project: its sources changed, but its output carries the
//     mtime the cache gave it, so the compile never read the changed file.
//     The gate must report it, and it must name the file.
//
//   * the PLANTED ERROR, which is the acceptance of issue #2100 in one
//     shape: a project whose changed source does not compile at all, with
//     the outputs the cache left behind. Nothing about that tree says "this
//     will not compile" except the compiler itself -- and the compiler never
//     ran, because the compile SKIPPED the project. The gate is what turns
//     that into a red lane.
//
// and the shapes that must stay quiet: a project that WAS recompiled, a
// tree where nothing changed, a project whose shared source did not change.
//
// The fixtures are SYNTHETIC trees in a temporary directory, and the
// decision is driven through the same evaluate() the real run uses, so the
// self-test exercises the decision and not a copy of it.

package checkcompiledtree

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"harbor.local/gates/internal/gate"
)

// selfTestRun runs the fixtures and reports what they proved.
func selfTestRun() int {
	failures := []string{}

	// Fixture 1: the project was SKIPPED. The gate must report it.
	failures = append(failures, checkSkippedFixture()...)

	// Fixture 2: the project carried a COMPILE ERROR and was skipped. This
	// is the acceptance of #2100: the error is in the tree, the outputs
	// came from the cache, and the lane would have been green.
	failures = append(failures, checkPlantedErrorFixture()...)

	// Fixture 3: the project was RECOMPILED. The gate must stay quiet.
	failures = append(failures, checkRecompiledFixture()...)

	// Fixture 4: the project was skipped and has NO build output at all.
	failures = append(failures, checkMissingOutputFixture()...)

	// Fixture 5: nothing changed. The gate must stay quiet, or it would
	// fire on every lane that touches no source.
	failures = append(failures, checkUnchangedFixture()...)

	// Fixture 6: the shared source. A file that lives outside the project
	// directory and is compiled into several projects at once: a change to
	// it must rebuild every project that names it.
	failures = append(failures, checkSharedSourceFixture()...)

	// Fixture 7: a file the project REMOVES from its compile items. A
	// change to it must NOT rebuild the project, or the gate would cry wolf
	// on every pull request that touches a file the project does not
	// compile.
	failures = append(failures, checkRemovedSourceFixture()...)

	if len(failures) > 0 {
		for _, failure := range failures {
			fmt.Fprintf(os.Stderr, "check-compiled-tree self-test: %s\n", failure)
		}
		fmt.Fprintf(os.Stderr,
			"check-compiled-tree self-test: %d fixture(s) did not hold\n", len(failures))
		return gate.Red
	}
	fmt.Println("check-compiled-tree self-test: ok")
	return gate.Green
}

// checkSkippedFixture: a changed project whose output the cache left behind.
func checkSkippedFixture() []string {
	tree, start, cleanup := skippedTree(nil)
	defer cleanup()

	projects, err := readProjects(tree.root, []string{"src/Widget/Widget.csproj"})
	if err != nil {
		return []string{fmt.Sprintf("the skipped fixture could not be read: %v", err)}
	}

	changed := map[string]bool{"src/Widget/Widget.cs": true}
	findings, _, err := evaluate(projects, changed, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the skipped fixture could not be evaluated: %v", err)}
	}
	if len(findings) != 1 {
		return []string{fmt.Sprintf(
			"the skipped fixture produced %d findings, expected 1: a project whose sources "+
				"changed and whose output the cache left behind must be reported",
			len(findings))}
	}
	if !strings.Contains(findings[0].reason, "SKIPPED") {
		return []string{fmt.Sprintf(
			"the skipped fixture reported %q, and the reason must say the compile SKIPPED "+
				"the project", findings[0].reason)}
	}
	if !strings.Contains(findings[0].changed, "Widget.cs") {
		return []string{fmt.Sprintf(
			"the skipped fixture named %q, and the finding must name the file that changed",
			findings[0].changed)}
	}
	if !strings.Contains(findings[0].project, "Widget.csproj") {
		return []string{fmt.Sprintf(
			"the skipped fixture named the project %q, and the finding must name the project",
			findings[0].project)}
	}
	return nil
}

// checkPlantedErrorFixture: a project whose changed source does not compile
// and whose output came from the cache.
//
// This is the shape issue #2100 describes, and the fixture is the honest
// version of it: the error is REAL (the fixture's source is not valid C#),
// and the output is the one the cache carried.
//
// The gate cannot compile C# -- that is not its job, and the compiler
// already refuses to build a tree with an error in it. What the gate proves
// is the part the compiler CANNOT see: that the project the error lives in
// was never handed to the compiler at all, because its output came from the
// cache and the compile skipped it.
//
// The gate finds it by CONTENT, not by mtime: the changed set is the set of
// files whose content differs from the commit the outputs were built from.
func checkPlantedErrorFixture() []string {
	tree, start, cleanup := skippedTree([]string{
		"this is not valid C# and the compiler would say so",
	})
	defer cleanup()

	projects, err := readProjects(tree.root, []string{"src/Widget/Widget.csproj"})
	if err != nil {
		return []string{fmt.Sprintf("the planted-error fixture could not be read: %v", err)}
	}

	changed := map[string]bool{"src/Widget/Widget.cs": true}
	findings, _, err := evaluate(projects, changed, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the planted-error fixture could not be evaluated: %v", err)}
	}
	if len(findings) != 1 {
		return []string{fmt.Sprintf(
			"the planted-error fixture produced %d findings, expected 1: a project with a "+
				"compile error whose outputs came from the cache must be reported, or the "+
				"lane ships a broken tree and calls it green",
			len(findings))}
	}
	if !strings.Contains(findings[0].reason, "never read") {
		return []string{fmt.Sprintf(
			"the planted-error fixture reported %q, and the reason must say the changed "+
				"file was never read", findings[0].reason)}
	}
	return nil
}

// checkRecompiledFixture: a changed project that WAS recompiled.
func checkRecompiledFixture() []string {
	tree, start, cleanup := skippedTree(nil)
	defer cleanup()

	projects, err := readProjects(tree.root, []string{"src/Widget/Widget.csproj"})
	if err != nil {
		return []string{fmt.Sprintf("the recompiled fixture could not be read: %v", err)}
	}

	// The compile produced a new output AFTER the build started.
	output := filepath.Join(tree.root, "src/Widget/obj/Release/net10.0/Widget.dll")
	later := start.Add(30 * time.Second)
	if err := os.Chtimes(output, later, later); err != nil {
		return []string{fmt.Sprintf("the recompiled fixture could not be restamped: %v", err)}
	}

	changed := map[string]bool{"src/Widget/Widget.cs": true}
	findings, _, err := evaluate(projects, changed, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the recompiled fixture could not be evaluated: %v", err)}
	}
	if len(findings) != 0 {
		return []string{fmt.Sprintf(
			"the recompiled fixture produced %d findings, expected 0: a project that WAS "+
				"recompiled must not be reported", len(findings))}
	}
	return nil
}

// checkMissingOutputFixture: a changed project with no output at all.
func checkMissingOutputFixture() []string {
	tree, start, cleanup := skippedTree(nil)
	defer cleanup()

	projects, err := readProjects(tree.root, []string{"src/Widget/Widget.csproj"})
	if err != nil {
		return []string{fmt.Sprintf("the missing-output fixture could not be read: %v", err)}
	}

	if err := os.Remove(filepath.Join(tree.root, "src/Widget/obj/Release/net10.0/Widget.dll")); err != nil {
		return []string{fmt.Sprintf("the missing-output fixture could not remove the output: %v", err)}
	}

	changed := map[string]bool{"src/Widget/Widget.cs": true}
	findings, _, err := evaluate(projects, changed, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the missing-output fixture could not be evaluated: %v", err)}
	}
	if len(findings) != 1 {
		return []string{fmt.Sprintf(
			"the missing-output fixture produced %d findings, expected 1: a changed project "+
				"with no output at all was never compiled", len(findings))}
	}
	return nil
}

// checkUnchangedFixture: nothing changed, so nothing is reported.
func checkUnchangedFixture() []string {
	tree, start, cleanup := skippedTree(nil)
	defer cleanup()

	projects, err := readProjects(tree.root, []string{"src/Widget/Widget.csproj"})
	if err != nil {
		return []string{fmt.Sprintf("the unchanged fixture could not be read: %v", err)}
	}

	findings, checked, err := evaluate(projects, map[string]bool{}, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the unchanged fixture could not be evaluated: %v", err)}
	}
	if len(findings) != 0 {
		return []string{fmt.Sprintf(
			"the unchanged fixture produced %d findings, expected 0: a lane that changes no "+
				"source must not be reported", len(findings))}
	}
	if checked != 0 {
		return []string{fmt.Sprintf(
			"the unchanged fixture checked %d projects, expected 0", checked)}
	}
	return nil
}

// checkSharedSourceFixture: a shared source, compiled into two projects.
func checkSharedSourceFixture() []string {
	tree, start, cleanup := skippedTree(nil)
	defer cleanup()

	// The shared source itself.
	sharedDir := filepath.Join(tree.root, "src/Shared")
	if err := os.MkdirAll(sharedDir, 0o755); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be created: %v", err)}
	}
	if err := os.WriteFile(filepath.Join(sharedDir, "Shared.cs"), []byte("namespace Shared;\n"), 0o644); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be written: %v", err)}
	}

	// The first project names the shared file too, so the fixture has two
	// projects that compile it and one that does not.
	shared := "src/Shared/Shared.cs"
	first := "src/Widget/Widget.csproj"
	withShared := "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
		"  <PropertyGroup>\n" +
		"    <TargetFramework>net10.0</TargetFramework>\n" +
		"  </PropertyGroup>\n" +
		"  <ItemGroup>\n" +
		"    <Compile Include=\"..\\Shared\\Shared.cs\" LinkBase=\"Shared\" />\n" +
		"  </ItemGroup>\n" +
		"</Project>\n"
	if err := os.WriteFile(filepath.Join(tree.root, first), []byte(withShared), 0o644); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be written: %v", err)}
	}
	second := "src/Other/Other.csproj"
	if err := os.MkdirAll(filepath.Join(tree.root, "src/Other"), 0o755); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be created: %v", err)}
	}
	other := "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
		"  <ItemGroup>\n" +
		"    <Compile Include=\"..\\Shared\\Shared.cs\" LinkBase=\"Shared\" />\n" +
		"  </ItemGroup>\n" +
		"</Project>\n"
	if err := os.WriteFile(filepath.Join(tree.root, second), []byte(other), 0o644); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be written: %v", err)}
	}
	otherOutput := filepath.Join(tree.root, "src/Other/obj/Release/net10.0/Other.dll")
	if err := os.MkdirAll(filepath.Dir(otherOutput), 0o755); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be created: %v", err)}
	}
	if err := os.WriteFile(otherOutput, []byte("cached"), 0o644); err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be written: %v", err)}
	}

	paths := []string{first, second}
	projects, err := readProjects(tree.root, paths)
	if err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be read: %v", err)}
	}

	changed := map[string]bool{shared: true}
	findings, checked, err := evaluate(projects, changed, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the shared-source fixture could not be evaluated: %v", err)}
	}
	if checked != 2 {
		return []string{fmt.Sprintf(
			"the shared-source fixture checked %d projects, expected 2: a shared source is "+
				"compiled into every project that names it", checked)}
	}
	if len(findings) != 2 {
		return []string{fmt.Sprintf(
			"the shared-source fixture produced %d findings, expected 2: both projects that "+
				"compile the shared source must be reported", len(findings))}
	}
	return nil
}

// checkRemovedSourceFixture: a file the project removes from its compile
// items.
//
// A change to it must not rebuild the project, or the gate would cry wolf
// on every pull request that touches such a file.
func checkRemovedSourceFixture() []string {
	tree, start, cleanup := skippedTree(nil)
	defer cleanup()

	removed := "src/Widget/Removed.cs"
	if err := os.WriteFile(filepath.Join(tree.root, removed), []byte("// not compiled\n"), 0o644); err != nil {
		return []string{fmt.Sprintf("the removed-source fixture could not be written: %v", err)}
	}
	project := "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
		"  <PropertyGroup>\n" +
		"    <TargetFramework>net10.0</TargetFramework>\n" +
		"  </PropertyGroup>\n" +
		"  <ItemGroup>\n" +
		"    <Compile Remove=\"Removed.cs\" />\n" +
		"  </ItemGroup>\n" +
		"</Project>\n"
	if err := os.WriteFile(filepath.Join(tree.root, "src/Widget/Widget.csproj"), []byte(project), 0o644); err != nil {
		return []string{fmt.Sprintf("the removed-source fixture could not be written: %v", err)}
	}

	projects, err := readProjects(tree.root, []string{"src/Widget/Widget.csproj"})
	if err != nil {
		return []string{fmt.Sprintf("the removed-source fixture could not be read: %v", err)}
	}

	changed := map[string]bool{removed: true}
	findings, checked, err := evaluate(projects, changed, start, tree.root, "Release")
	if err != nil {
		return []string{fmt.Sprintf("the removed-source fixture could not be evaluated: %v", err)}
	}
	if len(findings) != 0 {
		return []string{fmt.Sprintf(
			"the removed-source fixture produced %d findings, expected 0: a file the project "+
				"removes from its compile items does not rebuild the project", len(findings))}
	}
	if checked != 0 {
		return []string{fmt.Sprintf(
			"the removed-source fixture checked %d projects, expected 0", checked)}
	}
	return nil
}

// fixtureTree is a synthetic tree the fixtures drive.
type fixtureTree struct {
	root string
}

// skippedTree builds a synthetic tree with one project whose output carries
// the mtime the cache gave it.
//
// The output is stamped BEFORE the build start, which is the whole shape of
// the failure: the compile never wrote this file, so the file in the tree is
// the one the cache carried.
//
// The source lines, when given, replace the source of the project: the
// planted-error fixture uses them to put a compile error in the tree.
func skippedTree(sourceLines []string) (fixtureTree, time.Time, func()) {
	root, err := os.MkdirTemp("", "compiled-tree-gate-")
	if err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}

	projectDir := filepath.Join(root, "src/Widget")
	if err := os.MkdirAll(projectDir, 0o755); err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}

	source := "namespace Widget;\npublic sealed class Widget { }\n"
	if len(sourceLines) > 0 {
		source = strings.Join(sourceLines, "\n") + "\n"
	}
	if err := os.WriteFile(filepath.Join(projectDir, "Widget.cs"), []byte(source), 0o644); err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}
	project := "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
		"  <PropertyGroup>\n" +
		"    <TargetFramework>net10.0</TargetFramework>\n" +
		"  </PropertyGroup>\n" +
		"</Project>\n"
	if err := os.WriteFile(filepath.Join(projectDir, "Widget.csproj"), []byte(project), 0o644); err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}

	outputDir := filepath.Join(projectDir, "obj/Release/net10.0")
	if err := os.MkdirAll(outputDir, 0o755); err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}
	output := filepath.Join(outputDir, "Widget.dll")
	if err := os.WriteFile(output, []byte("cached"), 0o644); err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}

	// The build start: a moment after the cache's stamp, so the cached
	// output is older than the build and the project counts as skipped.
	start := time.Now().Truncate(time.Second).Add(10 * time.Second)
	cached := start.Add(-5 * time.Second)
	if err := os.Chtimes(output, cached, cached); err != nil {
		return fixtureTree{}, time.Time{}, func() {}
	}

	cleanup := func() { os.RemoveAll(root) }
	return fixtureTree{root: root}, start, cleanup
}
