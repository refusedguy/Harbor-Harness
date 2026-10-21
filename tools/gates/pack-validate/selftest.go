package packvalidate

// selftest.go -- the gate's own proof that it can fail.
//
// The fixture is a throwaway tree with one packable project and one package
// that is built three ways: good, good-without-a-namespace, and broken. The
// good package must pass, the namespace-less one must pass too (the checks
// do not care which xmlns the manifest carries), and the broken one must be
// rejected for the reasons it is broken.
//
// A validator that accepts everything is not a validator, and the broken
// package is the control.

import (
	"archive/zip"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

const (
	fixtureVersion = "0.4.0-alpha"
	fixturePkgID   = "Harbor.Demo"
)

// writeFile writes a file, creating the directories above it.
func writeFile(path, text string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(path, []byte(text), 0o644)
}

// makeNupkg builds a package with the given tags, with or without a
// README, and with or without a namespace on the manifest.
func makeNupkg(path, tags string, readme bool, xmlns string) error {
	nuspec := fmt.Sprintf(`<package%s>`+
		`<metadata><id>%s</id><version>%s</version>`+
		`<description>demo</description>`+
		`<license type="expression">MIT</license>`+
		`<repository type="git" url="%s" />`+
		`<projectUrl>%s</projectUrl>`+
		`<tags>%s</tags></metadata></package>`,
		xmlns, fixturePkgID, fixtureVersion, repoURL, repoURL, tags)

	file, err := os.Create(path)
	if err != nil {
		return err
	}
	defer file.Close()
	writer := zip.NewWriter(file)
	add := func(name string, body []byte) error {
		entry, err := writer.CreateHeader(&zip.FileHeader{Name: name, Method: zip.Store})
		if err != nil {
			return err
		}
		_, err = entry.Write(body)
		return err
	}
	if err := add(fixturePkgID+".nuspec", []byte(nuspec)); err != nil {
		return err
	}
	if err := add("lib/net10.0/Harbor.Demo.dll", []byte("dll")); err != nil {
		return err
	}
	if readme {
		if err := add("README.md", []byte("readme")); err != nil {
			return err
		}
	}
	return writer.Close()
}

// selfTest runs the fixture and asserts the three verdicts.
func selfTest() int {
	root, err := os.MkdirTemp("", "pack-validate-")
	if err != nil {
		fmt.Printf("selftest FAIL: cannot create the fixture: %v\n", err)
		return gate.Red
	}
	defer os.RemoveAll(root)

	if err := writeFile(filepath.Join(root, "Directory.Build.props"),
		"<Project><PropertyGroup><Version>"+fixtureVersion+
			"</Version></PropertyGroup></Project>"); err != nil {
		fmt.Printf("selftest FAIL: cannot write the fixture: %v\n", err)
		return gate.Red
	}
	if err := writeFile(filepath.Join(root, "src", "Harbor.Demo", "Harbor.Demo.csproj"),
		"<Project><PropertyGroup><IsPackable>true</IsPackable>"+
			"<PackageId>Harbor.Demo</PackageId></PropertyGroup></Project>"); err != nil {
		fmt.Printf("selftest FAIL: cannot write the fixture: %v\n", err)
		return gate.Red
	}
	out := filepath.Join(root, "out")
	if err := os.MkdirAll(out, 0o755); err != nil {
		fmt.Printf("selftest FAIL: cannot create the fixture: %v\n", err)
		return gate.Red
	}
	packagePath := filepath.Join(out, "Harbor.Demo."+fixtureVersion+".nupkg")

	// 1. A good package passes.
	if err := makeNupkg(packagePath, "harbor;demo;test", true,
		` xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"`); err != nil {
		fmt.Printf("selftest FAIL: cannot build the fixture package: %v\n", err)
		return gate.Red
	}
	errs, err := validate(out, filepath.Join(root, "src"))
	if err != nil {
		fmt.Printf("selftest FAIL: good package rejected: %v\n", err)
		return gate.Red
	}
	if len(errs) > 0 {
		fmt.Printf("selftest FAIL: good package rejected: %s\n", pycompat.ListRepr(errs))
		return gate.Red
	}

	// 2. A namespace-less manifest passes too: the checks do not care which
	//    xmlns the manifest carries.
	if err := makeNupkg(packagePath, "harbor;demo;test", true, ""); err != nil {
		fmt.Printf("selftest FAIL: cannot build the fixture package: %v\n", err)
		return gate.Red
	}
	errs, err = validate(out, filepath.Join(root, "src"))
	if err != nil {
		fmt.Printf("selftest FAIL: xmlns-less package rejected: %v\n", err)
		return gate.Red
	}
	if len(errs) > 0 {
		fmt.Printf("selftest FAIL: xmlns-less package rejected: %s\n", pycompat.ListRepr(errs))
		return gate.Red
	}

	// 3. A broken package is rejected, and the reasons name the missing tags
	//    and the missing README.
	if err := makeNupkg(packagePath, "lonely", false, ""); err != nil {
		fmt.Printf("selftest FAIL: cannot build the fixture package: %v\n", err)
		return gate.Red
	}
	errs, err = validate(out, filepath.Join(root, "src"))
	if err != nil {
		fmt.Printf("selftest FAIL: bad package accepted: %v\n", err)
		return gate.Red
	}
	if len(errs) < 2 || !anyContains(errs, "tag") || !anyContains(errs, "README") {
		fmt.Printf("selftest FAIL: bad package accepted: %s\n", pycompat.ListRepr(errs))
		return gate.Red
	}

	fmt.Println("selftest: ok")
	return gate.Green
}

func anyContains(items []string, needle string) bool {
	for _, item := range items {
		if strings.Contains(item, needle) {
			return true
		}
	}
	return false
}
