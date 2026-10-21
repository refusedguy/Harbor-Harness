package testdup

// groups.go -- the duplicate groups and the census.

import (
	"crypto/sha1"
	"encoding/hex"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// sha1Hex is the SHA-1 of a text, lower-case hex.
func sha1Hex(text string) string {
	sum := sha1.Sum([]byte(text))
	return hex.EncodeToString(sum[:])
}

// The scope of the gate.
const (
	scopeDir      = "tests"
	baselineName  = "test-dup-baseline.json"
	minBodyLines  = 6
	minMembers    = 2
	minSimilarity = 0.50
)

// excludedDirs are the build output directories.
var excludedDirs = []string{"bin", "obj"}

// excludedPaths are the trees the gate does not scan.
var excludedPaths = []string{"tests/Harbor.Architecture.Tests"}

// dupGroup is one group of duplicated bodies.
type dupGroup struct {
	fingerprint    string
	memberKey      string
	memberKeys     []string
	members        []*method
	count          int
	exactCount     int
	files          []string
	fileCount      int
	lines          int
	duplicateLines int
	intraFile      bool
	isTest         bool
	similarity     float64
}

// scanCounts are the counters of the scan.
type scanCounts struct {
	files       int
	lines       int
	methods     int
	testMethods int
}

// censusCounts are the headline numbers.
type censusCounts struct {
	groups            int
	intraFileGroups   int
	crossFileGroups   int
	exactGroups       int
	duplicateLines    int
	groupLines        int
	shapeCoincidences int
	files             int
	lines             int
	methods           int
	testMethods       int
}

// isExcluded reports whether a path is out of the gate's scope.
func isExcluded(path string) bool {
	for _, part := range strings.Split(path, "/") {
		for _, excluded := range excludedDirs {
			if part == excluded {
				return true
			}
		}
	}
	for _, excluded := range excludedPaths {
		if path == excluded || strings.HasPrefix(path, excluded+"/") {
			return true
		}
	}
	return false
}

// scanFiles returns the repo-relative paths of every scanned .cs file,
// sorted.
func scanFiles(root string) []string {
	base := filepath.Join(root, scopeDir)
	var found []string
	_ = filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			name := info.Name()
			for _, excluded := range excludedDirs {
				if name == excluded {
					return filepath.SkipDir
				}
			}
			return nil
		}
		if !strings.HasSuffix(info.Name(), ".cs") {
			return nil
		}
		relative, err := filepath.Rel(root, path)
		if err != nil {
			return nil
		}
		relative = filepath.ToSlash(relative)
		if isExcluded(relative) {
			return nil
		}
		found = append(found, relative)
		return nil
	})
	sort.Strings(found)
	return found
}

// scanTree returns every method body in the scanned tree, plus the census
// counters.
func scanTree(root string) ([]*method, scanCounts) {
	var methods []*method
	var counts scanCounts
	for _, relative := range scanFiles(root) {
		data, err := os.ReadFile(filepath.Join(root, relative))
		if err != nil {
			continue
		}
		src := string(data)
		counts.files++
		counts.lines += strings.Count(src, "\n") + 1
		found := extractMethods(relative, src)
		methods = append(methods, found...)
	}
	counts.methods = len(methods)
	for _, one := range methods {
		if one.isTest {
			counts.testMethods++
		}
	}
	return methods, counts
}

// jaccard is the Jaccard index of two identifier vocabularies.
func jaccard(left map[string]bool, right map[string]bool) float64 {
	intersection := 0
	for name := range left {
		if right[name] {
			intersection++
		}
	}
	union := len(left) + len(right) - intersection
	if union == 0 {
		return 1.0
	}
	return float64(intersection) / float64(union)
}

// buildGroups returns the duplicate groups, keyed by the shape fingerprint
// of the body.
//
// A group is a set of methods with the same normalised shape. It counts as
// a duplicate only when its members also share enough of their identifier
// vocabulary: two bodies can have the same token shape and nothing else in
// common, and a coincidence is not a duplicate.
func buildGroups(methods []*method, minLines int, minMembers int, minSimilarity float64) ([]*dupGroup, int) {
	buckets := map[string][]*method{}
	var order []string
	for _, one := range methods {
		if one.lines < minLines {
			continue
		}
		if _, ok := buckets[one.shape]; !ok {
			order = append(order, one.shape)
		}
		buckets[one.shape] = append(buckets[one.shape], one)
	}
	var groups []*dupGroup
	coincidences := 0
	for _, shape := range order {
		members := buckets[shape]
		if len(members) < minMembers {
			continue
		}
		worst := 1.0
		for i := 0; i < len(members); i++ {
			for j := i + 1; j < len(members); j++ {
				similarity := jaccard(members[i].idents, members[j].idents)
				if similarity < worst {
					worst = similarity
				}
				if worst < minSimilarity {
					break
				}
			}
			if worst < minSimilarity {
				break
			}
		}
		if worst < minSimilarity {
			coincidences++
			continue
		}
		exactBuckets := map[string][]*method{}
		for _, member := range members {
			exactBuckets[member.exact] = append(exactBuckets[member.exact], member)
		}
		exactMembers := 0
		for _, value := range exactBuckets {
			if len(value) >= 2 {
				exactMembers += len(value)
			}
		}
		fileSet := map[string]bool{}
		for _, member := range members {
			fileSet[member.path] = true
		}
		files := make([]string, 0, len(fileSet))
		for path := range fileSet {
			files = append(files, path)
		}
		sort.Strings(files)
		memberKeys := make([]string, 0, len(members))
		for _, member := range members {
			memberKeys = append(memberKeys, member.path+"::"+member.name+"#"+itoa(member.ordinal))
		}
		sort.Strings(memberKeys)
		sortedMembers := append([]*method(nil), members...)
		sort.SliceStable(sortedMembers, func(i, j int) bool {
			if sortedMembers[i].path != sortedMembers[j].path {
				return sortedMembers[i].path < sortedMembers[j].path
			}
			return sortedMembers[i].startLine < sortedMembers[j].startLine
		})
		isTest := true
		for _, member := range members {
			if !member.isTest {
				isTest = false
			}
		}
		groups = append(groups, &dupGroup{
			fingerprint:    shape,
			memberKey:      sha1Hex(strings.Join(memberKeys, "\n")),
			memberKeys:     memberKeys,
			members:        sortedMembers,
			count:          len(members),
			exactCount:     exactMembers,
			files:          files,
			fileCount:      len(files),
			lines:          members[0].lines,
			duplicateLines: (len(members) - 1) * members[0].lines,
			intraFile:      len(files) == 1,
			isTest:         isTest,
			similarity:     roundTo(worst, 3),
		})
	}
	sort.SliceStable(groups, func(i, j int) bool {
		if groups[i].duplicateLines != groups[j].duplicateLines {
			return groups[i].duplicateLines > groups[j].duplicateLines
		}
		if groups[i].count != groups[j].count {
			return groups[i].count > groups[j].count
		}
		return groups[i].fingerprint < groups[j].fingerprint
	})
	return groups, coincidences
}

// census returns the headline numbers: groups, and the lines they make
// removable.
func census(groups []*dupGroup, scan scanCounts, coincidences int) censusCounts {
	counts := censusCounts{
		groups:            len(groups),
		exactGroups:       0,
		shapeCoincidences: coincidences,
		files:             scan.files,
		lines:             scan.lines,
		methods:           scan.methods,
		testMethods:       scan.testMethods,
	}
	for _, group := range groups {
		if group.intraFile {
			counts.intraFileGroups++
		} else {
			counts.crossFileGroups++
		}
		if group.exactCount >= 2 {
			counts.exactGroups++
		}
		counts.duplicateLines += group.duplicateLines
		counts.groupLines += group.count * group.lines
	}
	return counts
}
