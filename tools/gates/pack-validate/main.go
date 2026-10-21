// Package packvalidate is the compiled port of tools/pack-validate.py.
//
// It validates the packed Harbor.* NuGet packages: every packable src/
// project must have produced a package, and every package must carry its
// metadata, its MIT licence expression, its repository url, its README and
// its lib/net10.0 assemblies.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package packvalidate

import (
	"archive/zip"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
	"harbor.local/gates/internal/xmltree"
)

const (
	repoURL  = "https://github.com/refusedguy/Harbor-Harness"
	licence  = "MIT"
	minTags  = 3
	toolName = "pack-validate"
)

var (
	versionRe = regexp.MustCompile(`<Version>([^<]+)</Version>`)
	packageRe = regexp.MustCompile(`<PackageId>([^<]+)</PackageId>`)
)

// Run is the gate entry point.
//
// The python original took the packages directory and the src directory as
// POSITIONAL arguments, and the flags it knew were `--selftest`. The flags
// and the defaults are the same here.
func Run(args []string) int {
	for _, arg := range args {
		if arg == "--selftest" {
			return selfTest()
		}
	}

	packages := "out/packages"
	if len(args) > 0 {
		packages = args[0]
	}
	src := "src"
	if len(args) > 1 {
		src = args[1]
	}

	if info, err := os.Stat(packages); err != nil || !info.IsDir() {
		fmt.Printf("error: packages dir %s does not exist\n", packages)
		return gate.Red
	}

	errs, err := validate(packages, src)
	if err != nil {
		// The python original raised SystemExit with this message, which
		// prints it on stderr and exits 1.
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	if len(errs) > 0 {
		fmt.Printf("%d package problem(s):\n", len(errs))
		for _, e := range errs {
			fmt.Printf("  - %s\n", e)
		}
		return gate.Red
	}
	ids, err := expectedIDs(src)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	fmt.Printf("%s: ok (%d packages)\n", toolName, len(ids))
	return gate.Green
}

// repoVersion reads the version out of Directory.Build.props.
//
// The python original raised SystemExit when the file carried no <Version>,
// and that is an exit code 1 with the message on stderr.
func repoVersion(root string) (string, error) {
	data, err := os.ReadFile(filepath.Join(root, "Directory.Build.props"))
	if err != nil {
		return "", fmt.Errorf("cannot read Directory.Build.props: %w", err)
	}
	match := versionRe.FindSubmatch(data)
	if match == nil {
		return "", fmt.Errorf("cannot find <Version> in Directory.Build.props")
	}
	return strings.TrimSpace(string(match[1])), nil
}

// packageID is the <PackageId> of a project, or its file name.
func packageID(csproj string) string {
	data, err := os.ReadFile(csproj)
	if err != nil {
		return strings.TrimSuffix(filepath.Base(csproj), filepath.Ext(csproj))
	}
	match := packageRe.FindSubmatch(data)
	if match == nil {
		return strings.TrimSuffix(filepath.Base(csproj), filepath.Ext(csproj))
	}
	return strings.TrimSpace(string(match[1]))
}

// expectedIDs lists the packable projects under src, in the order the
// python original listed them (python's sorted() over pathlib paths, which
// compares the path's PARTS).
func expectedIDs(src string) ([]string, error) {
	entries, err := os.ReadDir(src)
	if err != nil {
		return nil, nil
	}
	var projects []string
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		inner, err := os.ReadDir(filepath.Join(src, entry.Name()))
		if err != nil {
			continue
		}
		for _, file := range inner {
			if file.IsDir() || !strings.HasSuffix(file.Name(), ".csproj") {
				continue
			}
			projects = append(projects, filepath.Join(src, entry.Name(), file.Name()))
		}
	}
	projects = pycompat.SortPaths(projects)

	var ids []string
	for _, project := range projects {
		data, err := os.ReadFile(project)
		if err != nil {
			continue
		}
		if strings.Contains(string(data), "<IsPackable>true</IsPackable>") {
			ids = append(ids, packageID(project))
		}
	}
	return ids, nil
}

// validate is the whole check: the problems it finds, or an error when it
// could not read what it needs.
func validate(packages, src string) ([]string, error) {
	root := "."
	if filepath.Base(src) == "src" {
		root = filepath.Dir(src)
		if root == "" {
			root = "."
		}
	}
	version, err := repoVersion(root)
	if err != nil {
		return nil, err
	}
	ids, err := expectedIDs(src)
	if err != nil {
		return nil, err
	}
	if len(ids) == 0 {
		return []string{"no IsPackable=true projects found under " + src}, nil
	}

	var errs []string
	have := map[string]bool{}
	entries, err := os.ReadDir(packages)
	if err != nil {
		return nil, fmt.Errorf("cannot read the packages dir %s: %w", packages, err)
	}
	for _, entry := range entries {
		if !entry.IsDir() && strings.HasSuffix(entry.Name(), ".nupkg") {
			have[entry.Name()] = true
		}
	}

	for _, pkgID := range ids {
		name := fmt.Sprintf("%s.%s.nupkg", pkgID, version)
		if !have[name] {
			errs = append(errs, fmt.Sprintf("%s: missing %s", pkgID, name))
			continue
		}
		reader, err := zip.OpenReader(filepath.Join(packages, name))
		if err != nil {
			errs = append(errs, fmt.Sprintf("%s: cannot open %s", pkgID, name))
			continue
		}
		nuspecName, nuspecData, ok := pickNuspec(reader, pkgID)
		if !ok {
			reader.Close()
			errs = append(errs, fmt.Sprintf("%s: no .nuspec inside %s", pkgID, name))
			continue
		}
		errs = append(errs, checkNuspec(nuspecData, pkgID, version)...)
		errs = append(errs, checkFiles(reader, pkgID)...)
		reader.Close()
		_ = nuspecName
	}

	expected := map[string]bool{}
	for _, id := range ids {
		expected[fmt.Sprintf("%s.%s.nupkg", id, version)] = true
	}
	var extra []string
	for name := range have {
		if !expected[name] {
			extra = append(extra, name)
		}
	}
	sort.Strings(extra)
	for _, name := range extra {
		fmt.Printf("info: ignoring non-src package %s\n", name)
	}
	return errs, nil
}

// pickNuspec finds the manifest inside a package.
//
// The manifest itself is preferred: a package may carry other
// .nuspec-suffixed content files, and the first one that ends with
// ".nuspec" is not necessarily the manifest.
func pickNuspec(reader *zip.ReadCloser, pkgID string) (string, []byte, bool) {
	preferred := pkgID + ".nuspec"
	for _, file := range reader.File {
		if file.Name == preferred {
			data, err := readZipFile(file)
			if err != nil {
				return "", nil, false
			}
			return file.Name, data, true
		}
	}
	for _, file := range reader.File {
		if strings.HasSuffix(file.Name, ".nuspec") {
			data, err := readZipFile(file)
			if err != nil {
				return "", nil, false
			}
			return file.Name, data, true
		}
	}
	return "", nil, false
}

func readZipFile(file *zip.File) ([]byte, error) {
	reader, err := file.Open()
	if err != nil {
		return nil, err
	}
	defer reader.Close()
	return readAll(reader)
}

// readAll reads a stream to its end.
func readAll(reader io.Reader) ([]byte, error) {
	return io.ReadAll(reader)
}

// checkNuspec holds the manifest against what the packages must carry.
func checkNuspec(nuspecData []byte, pkgID, version string) []string {
	var errs []string
	root, err := xmltree.Parse(nuspecData)
	if err != nil {
		return []string{fmt.Sprintf("%s: the nuspec cannot be read", pkgID)}
	}
	metadata := root.ChildAny("metadata")
	if metadata == nil {
		return []string{fmt.Sprintf("%s: no metadata element in nuspec", pkgID)}
	}
	text := func(tag string) string { return metadata.ChildAny(tag).TrimmedText() }

	if text("id") != pkgID {
		errs = append(errs, fmt.Sprintf("%s: nuspec id is %s", pkgID, pycompat.Repr(text("id"))))
	}
	if text("version") != version {
		errs = append(errs, fmt.Sprintf("%s: nuspec version is %s, want %s",
			pkgID, pycompat.Repr(text("version")), pycompat.Repr(version)))
	}
	if text("description") == "" {
		errs = append(errs, fmt.Sprintf("%s: empty description", pkgID))
	}
	licenceNode := metadata.ChildAny("license")
	if licenceNode == nil || licenceNode.TrimmedText() != licence || licenceNode.Attr("type") != "expression" {
		errs = append(errs, fmt.Sprintf("%s: license is not expression:%s", pkgID, licence))
	}
	repo := metadata.ChildAny("repository")
	if repo == nil || repo.Attr("url") != repoURL || repo.Attr("type") != "git" {
		errs = append(errs, fmt.Sprintf("%s: repository must be type=git url=%s", pkgID, repoURL))
	}
	if !strings.Contains(text("projectUrl"), "refusedguy/Harbor-Harness") {
		errs = append(errs, fmt.Sprintf("%s: projectUrl missing the real repository", pkgID))
	}
	tags := splitTags(text("tags"))
	if len(tags) < minTags {
		errs = append(errs, fmt.Sprintf("%s: only %d tag(s), need >= %d", pkgID, len(tags), minTags))
	}
	return errs
}

// splitTags splits the tags field on whitespace, commas and semicolons.
func splitTags(raw string) []string {
	var out []string
	current := strings.Builder{}
	for _, r := range raw {
		switch r {
		case ' ', '\t', '\n', '\r', '\v', '\f', ';', ',':
			if current.Len() > 0 {
				out = append(out, current.String())
				current.Reset()
			}
		default:
			current.WriteRune(r)
		}
	}
	if current.Len() > 0 {
		out = append(out, current.String())
	}
	return out
}

// checkFiles holds the package's contents against what it must carry.
func checkFiles(reader *zip.ReadCloser, pkgID string) []string {
	var errs []string
	names := make([]string, 0, len(reader.File))
	for _, file := range reader.File {
		names = append(names, file.Name)
	}
	hasReadme := false
	hasLib := false
	for _, name := range names {
		if name == "README.md" {
			hasReadme = true
		}
		if strings.HasPrefix(name, "lib/net10.0/") && strings.HasSuffix(name, ".dll") {
			hasLib = true
		}
	}
	if !hasReadme {
		errs = append(errs, fmt.Sprintf("%s: README.md missing from package", pkgID))
	}
	if !hasLib {
		errs = append(errs, fmt.Sprintf("%s: no lib/net10.0/*.dll in package", pkgID))
	}
	return errs
}
