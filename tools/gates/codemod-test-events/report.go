package codemodtestevents

// report.go -- the JSON report the codemod writes.
//
// The report is written by hand, because python's json.dumps keeps the keys
// in the order the dict first saw them and Go's encoding/json sorts the
// keys of a map. The order is part of the file: a report is read by a
// human and diffed against the previous one.

import (
	"strconv"
	"strings"
	"unicode/utf16"
)

// jsonString renders a string the way python's json.dumps does with
// ensure_ascii=True: the non-ASCII characters become \uXXXX escapes.
func jsonString(s string) string {
	var b strings.Builder
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case '"':
			b.WriteString(`\"`)
		case '\\':
			b.WriteString(`\\`)
		case '\n':
			b.WriteString(`\n`)
		case '\r':
			b.WriteString(`\r`)
		case '\t':
			b.WriteString(`\t`)
		case '\b':
			b.WriteString(`\b`)
		case '\f':
			b.WriteString(`\f`)
		default:
			if r < 0x20 {
				b.WriteString(`\u00`)
				const hex = "0123456789abcdef"
				b.WriteByte(hex[(r>>4)&0xf])
				b.WriteByte(hex[r&0xf])
				continue
			}
			if r < 0x7f {
				b.WriteRune(r)
				continue
			}
			if r > 0xffff {
				high, low := utf16.EncodeRune(r)
				writeUnicodeEscape(&b, high)
				writeUnicodeEscape(&b, low)
				continue
			}
			writeUnicodeEscape(&b, r)
		}
	}
	b.WriteByte('"')
	return b.String()
}

func writeUnicodeEscape(b *strings.Builder, r rune) {
	const hex = "0123456789abcdef"
	b.WriteString(`\u`)
	b.WriteByte(hex[(r>>12)&0xf])
	b.WriteByte(hex[(r>>8)&0xf])
	b.WriteByte(hex[(r>>4)&0xf])
	b.WriteByte(hex[r&0xf])
}

// renderReport the whole report, the way python's json.dumps(indent=2)
// writes it.
func renderReport(r reportData) string {
	var b strings.Builder
	b.WriteString("{\n")
	b.WriteString("  \"mode\": " + jsonString(r.mode) + ",\n")
	b.WriteString("  \"files_changed\": " + strconv.Itoa(r.filesChanged) + ",\n")
	b.WriteString("  \"sites_rewritten\": " + strconv.Itoa(r.sitesRewritten) + ",\n")
	b.WriteString("  \"event_sites_rewritten\": " + strconv.Itoa(r.eventSites) + ",\n")
	b.WriteString("  \"message_sites_rewritten\": " + strconv.Itoa(r.messageSites) + ",\n")
	b.WriteString("  \"lines_before\": " + strconv.Itoa(r.linesBefore) + ",\n")
	b.WriteString("  \"lines_after\": " + strconv.Itoa(r.linesAfter) + ",\n")
	b.WriteString("  \"chars_before\": " + strconv.Itoa(r.charsBefore) + ",\n")
	b.WriteString("  \"chars_after\": " + strconv.Itoa(r.charsAfter) + ",\n")

	if len(r.referenceProjects) == 0 {
		b.WriteString("  \"projects_gaining_a_reference\": [],\n")
	} else {
		b.WriteString("  \"projects_gaining_a_reference\": [\n")
		for i, project := range r.referenceProjects {
			b.WriteString("    " + jsonString(project))
			if i < len(r.referenceProjects)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  ],\n")
	}

	if len(r.skippedReasons) == 0 {
		b.WriteString("  \"skipped_by_reason\": {},\n")
	} else {
		b.WriteString("  \"skipped_by_reason\": {\n")
		for i, reason := range r.skippedReasons {
			b.WriteString("    " + jsonString(reason) + ": " + strconv.Itoa(r.skippedCounts[i]))
			if i < len(r.skippedReasons)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  },\n")
	}

	if len(r.skippedSites) == 0 {
		b.WriteString("  \"skipped_sites\": [],\n")
	} else {
		b.WriteString("  \"skipped_sites\": [\n")
		for i, site := range r.skippedSites {
			b.WriteString("    {\n")
			b.WriteString("      \"file\": " + jsonString(site.file) + ",\n")
			b.WriteString("      \"line\": " + strconv.Itoa(site.line) + ",\n")
			b.WriteString("      \"type\": " + jsonString(site.typ) + ",\n")
			b.WriteString("      \"family\": " + jsonString(site.family) + ",\n")
			b.WriteString("      \"reason\": " + jsonString(site.reason) + "\n")
			b.WriteString("    }")
			if i < len(r.skippedSites)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  ],\n")
	}

	if len(r.files) == 0 {
		b.WriteString("  \"files\": []\n")
	} else {
		b.WriteString("  \"files\": [\n")
		for i, f := range r.files {
			b.WriteString("    {\n")
			b.WriteString("      \"path\": " + jsonString(f.path) + ",\n")
			b.WriteString("      \"sites\": " + strconv.Itoa(f.sites) + ",\n")
			b.WriteString("      \"lines_before\": " + strconv.Itoa(f.linesBefore) + ",\n")
			b.WriteString("      \"lines_after\": " + strconv.Itoa(f.linesAfter) + ",\n")
			b.WriteString("      \"chars_before\": " + strconv.Itoa(f.charsBefore) + ",\n")
			b.WriteString("      \"chars_after\": " + strconv.Itoa(f.charsAfter) + "\n")
			b.WriteString("    }")
			if i < len(r.files)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  ]\n")
	}

	b.WriteString("}")
	return b.String()
}
