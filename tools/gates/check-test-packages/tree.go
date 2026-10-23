package testpackages

// tree.go -- the projects of the scope, and the walk over them.
//
// The INDEX (`git ls-files`) rather than a glob, for the reason
// tools/md_gate.py states: an untracked scratch project must not be able to
// change what the gate sees, and a tracked project must not be able to hide
// from it. The walk is the fallback for a throwaway fixture tree, which is
// not a git repository.

import (
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

// scopeProjects returns every `.csproj` under the scope directories,
// repository-relative.
func scopeProjects(root string) []string {
	if tracked, ok := gitLsFiles(root, scopeDirTests, scopeDirSamples); ok && len(tracked) > 0 {
		return tracked
	}
	var found []string
	for _, scope := range []string{scopeDirTests, scopeDirSamples} {
		walkTree(filepath.Join(root, scope), func(name string) bool {
			return name != "bin" && name != "obj"
		}, func(path string, dirs []string, files []string) {
			for _, name := range files {
				if strings.HasSuffix(name, ".csproj") {
					found = append(found, relativeTo(root, filepath.Join(path, name)))
				}
			}
		})
	}
	sort.Strings(found)
	return found
}

// allProjects returns every `.csproj` CI restores, repository-relative.
//
// `contrib/` is excluded: it is unmaintained and not compiled by CI, so a
// lock file there would say nothing about the tree that ships.
func allProjects(root string) []string {
	if tracked, ok := gitLsFiles(root, "*.csproj"); ok && len(tracked) > 0 {
		var kept []string
		for _, path := range tracked {
			if strings.Split(path, "/")[0] == notRestored[0] {
				continue
			}
			kept = append(kept, path)
		}
		return kept
	}
	var found []string
	walkTree(root, func(name string) bool {
		return name != "bin" && name != "obj" && name != ".git"
	}, func(path string, dirs []string, files []string) {
		for _, name := range files {
			if !strings.HasSuffix(name, ".csproj") {
				continue
			}
			relative := relativeTo(root, filepath.Join(path, name))
			if strings.Split(relative, "/")[0] == notRestored[0] {
				continue
			}
			found = append(found, relative)
		}
	})
	sort.Strings(found)
	return found
}

// gitLsFiles lists the tracked paths, and reports whether git answered.
func gitLsFiles(root string, paths ...string) ([]string, bool) {
	arguments := append([]string{"ls-files", "--"}, paths...)
	command := exec.Command("git", arguments...)
	command.Dir = root
	command.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	out, err := command.Output()
	if err != nil {
		return nil, false
	}
	var tracked []string
	for _, line := range strings.Split(string(out), "\n") {
		if line == "" {
			continue
		}
		if strings.HasSuffix(line, ".csproj") {
			tracked = append(tracked, line)
		}
	}
	sort.Strings(tracked)
	return tracked, true
}

// walkTree walks a tree the way python's os.walk does: the directories in
// the order the filesystem returns them, and the files in the same order.
//
// The directories are filtered IN PLACE, so the recursion does not descend
// into the ones the filter dropped -- that is what os.walk's `dirnames[:] =`
// does.
func walkTree(root string, keep func(name string) bool, fn func(path string, dirs []string, files []string)) {
	var descend func(path string)
	descend = func(path string) {
		handle, err := os.Open(path)
		if err != nil {
			return
		}
		names, err := handle.Readdirnames(-1)
		handle.Close()
		if err != nil {
			return
		}
		var dirs, files []string
		for _, name := range names {
			info, err := os.Stat(filepath.Join(path, name))
			if err != nil {
				continue
			}
			if info.IsDir() {
				dirs = append(dirs, name)
			} else {
				files = append(files, name)
			}
		}
		filtered := dirs[:0]
		for _, name := range dirs {
			if keep != nil && !keep(name) {
				continue
			}
			filtered = append(filtered, name)
		}
		dirs = filtered
		fn(path, dirs, files)
		for _, name := range dirs {
			descend(filepath.Join(path, name))
		}
	}
	descend(root)
}

// relativeTo returns a path relative to the root, with '/' separators.
func relativeTo(root string, path string) string {
	relative, err := filepath.Rel(root, path)
	if err != nil {
		return filepath.ToSlash(path)
	}
	return filepath.ToSlash(relative)
}
