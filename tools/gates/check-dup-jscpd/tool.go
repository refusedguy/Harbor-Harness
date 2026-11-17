package dupjscpd

// tool.go -- the binary: fetch it, verify it, run it.
//
// The binary is a build-time tool: it never reaches the product and no
// PackageReference is added, so the dependency gate does not see it. It is
// fetched from the release, its sha256 is pinned per platform, and a
// download that does not match the pin is REFUSED before the tool runs.

import (
	"archive/tar"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"
)

// platformKey is the asset key for the machine we run on.
func platformKey() string {
	switch runtime.GOOS {
	case "linux":
		switch runtime.GOARCH {
		case "amd64":
			return "linux-x64"
		case "arm64":
			return "linux-arm64"
		}
	case "darwin":
		switch runtime.GOARCH {
		case "amd64":
			return "darwin-x64"
		case "arm64":
			return "darwin-arm64"
		}
	}
	return ""
}

// machineName is what python's platform.machine() would have said.
func machineName() string {
	if runtime.GOARCH == "amd64" {
		return "x86_64"
	}
	return runtime.GOARCH
}

// sha256Of is the sha256 of a file, lower-case hex.
func sha256Of(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}

// verifyArchive refuses an archive whose sha256 is not the pinned one.
func verifyArchive(archive string, expected string) error {
	actual, err := sha256Of(archive)
	if err != nil {
		return err
	}
	if actual != expected {
		return fmt.Errorf(
			"  sha256 mismatch for %s: expected %s, got %s. The download is refused and the tool is not run.",
			filepath.Base(archive), expected, actual)
	}
	return nil
}

// downloadTool fetches the pinned binary, verifies its sha256, and returns
// its path.
func downloadTool(cacheDir string, force bool) (string, error) {
	key := platformKey()
	asset, ok := toolAssets[key]
	if !ok {
		supported := make([]string, 0, len(toolAssets))
		for name := range toolAssets {
			supported = append(supported, name)
		}
		sort.Strings(supported)
		return "", fmt.Errorf(
			"  no pinned %s asset for %s/%s; the supported platforms are %s",
			toolName, runtime.GOOS, machineName(), strings.Join(supported, ", "))
	}
	name, expected := asset[0], asset[1]

	if err := os.MkdirAll(cacheDir, 0o755); err != nil {
		return "", err
	}
	archive := filepath.Join(cacheDir, name)
	need := force
	if !need {
		if _, err := os.Stat(archive); err != nil {
			need = true
		} else {
			actual, err := sha256Of(archive)
			if err != nil || actual != expected {
				need = true
			}
		}
	}
	if need {
		url := fmt.Sprintf("%s/v%s/%s", toolReleases, toolVersion, name)
		fmt.Printf("  downloading %s %s (%s)\n", toolName, toolVersion, name)
		payload, err := fetch(url)
		if err != nil {
			return "", err
		}
		if err := os.WriteFile(archive, payload, 0o644); err != nil {
			return "", err
		}
		if err := verifyArchive(archive, expected); err != nil {
			return "", err
		}
	}

	target := filepath.Join(cacheDir, toolName)
	binary, err := extractFromArchive(archive, toolName)
	if err != nil {
		return "", err
	}
	if err := os.WriteFile(target, binary, 0o755); err != nil {
		return "", err
	}
	if err := os.Chmod(target, 0o755); err != nil {
		return "", err
	}
	return target, nil
}

// fetch downloads a URL, with three attempts.
func fetch(url string) ([]byte, error) {
	client := &http.Client{Timeout: 120 * time.Second}
	var lastError error
	for attempt := 1; attempt <= 3; attempt++ {
		response, err := client.Get(url)
		if err == nil {
			defer response.Body.Close()
			payload, readError := io.ReadAll(response.Body)
			if readError != nil {
				err = readError
			} else {
				return payload, nil
			}
		}
		lastError = err
		fmt.Printf("  attempt %d failed: %s\n", attempt, err.Error())
	}
	return nil, fmt.Errorf("  could not download %s after 3 attempts: %s", url, lastError)
}

// extractFromArchive reads one member out of a gzipped tar.
func extractFromArchive(archive string, member string) ([]byte, error) {
	handle, err := os.Open(archive)
	if err != nil {
		return nil, err
	}
	defer handle.Close()
	decompressor, err := gzip.NewReader(handle)
	if err != nil {
		return nil, fmt.Errorf("  %s is not a gzipped tar: %s", filepath.Base(archive), err.Error())
	}
	defer decompressor.Close()
	reader := tar.NewReader(decompressor)
	for {
		header, err := reader.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("  %s is not a tarball: %s", filepath.Base(archive), err.Error())
		}
		if header.Name != member {
			continue
		}
		payload, err := io.ReadAll(reader)
		if err != nil {
			return nil, err
		}
		return payload, nil
	}
	return nil, fmt.Errorf("  %s carries no %s binary", filepath.Base(archive), member)
}

// runTool runs the tool and leaves its report behind.
//
// A stale report from an earlier run must never be read as this run's
// verdict: the run removes it first, and a run that produced no report at
// all is a failure, not a measurement.
func runTool(binary string, root string, reportPath string, config string, extraArgs []string) error {
	outputDir := filepath.Dir(reportPath)
	if outputDir == "" {
		outputDir = "."
	}
	if err := os.MkdirAll(outputDir, 0o755); err != nil {
		return err
	}
	command := []string{
		binary,
		"--config", config,
		"--reporters", "json",
		"--output", outputDir,
		"--report-name", strings.TrimSuffix(filepath.Base(reportPath), filepath.Ext(reportPath)),
	}
	command = append(command, extraArgs...)
	fmt.Printf("  $ %s\n", joinCommand(command))

	_ = os.Remove(reportPath)

	cmd := exec.Command(command[0], command[1:]...)
	cmd.Dir = root
	var stdout, stderr strings.Builder
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()
	code := 0
	if err != nil {
		exitError, ok := err.(*exec.ExitError)
		if !ok {
			return err
		}
		code = exitError.ExitCode()
	}
	if code != 0 {
		// jscpd writes its report BEFORE the fail-on-empty check, so a
		// scan that measured nothing still leaves a report to read. The
		// verdict belongs to the rules below, in the house diagnostic, not
		// to the tool's exit code.
		os.Stderr.WriteString(stdout.String())
		os.Stderr.WriteString(stderr.String())
		fmt.Printf("  %s exited with %d\n", toolName, code)
	}
	if _, err := os.Stat(reportPath); err != nil {
		return fmt.Errorf("  %s wrote no report at %s", toolName, reportPath)
	}
	return nil
}
