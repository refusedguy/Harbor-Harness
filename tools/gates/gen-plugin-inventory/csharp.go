package genplugininventory

import "strings"

// csharp.go -- the C# reader.
//
// The generator reads the plugin composition out of the C# sources as FILES,
// and the reader has to agree with the architecture rule that grades the
// document it writes: a rule that disagreed with its own generator would
// report a class that does not exist.
//
// The reader is string-aware: a `//` inside a string literal is not a
// comment, a brace inside a string literal is not a block, and a comment
// that mentions a class declaration is not a declaration.

// skipString is the index just past the regular string literal that starts
// at `i`.
func skipString(text string, i int) int {
	n := len(text)
	j := i + 1
	for j < n {
		if text[j] == '\\' {
			j += 2
			continue
		}
		if text[j] == '"' {
			return j + 1
		}
		j++
	}
	return n
}

// skipVerbatim is the index just past the verbatim string literal that starts
// at `i`.
//
// The opening quote is at `i`; a doubled quote inside is an escaped quote,
// not the end.
func skipVerbatim(text string, i int) int {
	n := len(text)
	j := i + 1
	for j < n {
		if text[j] == '"' {
			if j+1 < n && text[j+1] == '"' {
				j += 2
				continue
			}
			return j + 1
		}
		j++
	}
	return n
}

// skipRaw is the index just past the raw string literal that starts at `i`.
func skipRaw(text string, i int) int {
	end := strings.Index(text[i+3:], `"""`)
	if end < 0 {
		return len(text)
	}
	return i + 3 + end + 3
}

// skipChar is the index just past the character literal that starts at `i`.
func skipChar(text string, i int) int {
	n := len(text)
	j := i + 1
	for j < n {
		if text[j] == '\\' {
			j += 2
			continue
		}
		if text[j] == '\'' {
			return j + 1
		}
		j++
	}
	return n
}

// classBody is the body of a class, from the opening brace at `start`.
//
// Braces inside string literals and comments do not count: a verbatim string
// with a `{` in it is a character, not a block. A plugin that documents a
// template with braces would otherwise swallow the rest of the file.
func classBody(text string, start int) (string, bool) {
	depth := 0
	i := start
	n := len(text)
	for i < n {
		ch := text[i]
		if ch == '/' && i+1 < n && text[i+1] == '/' {
			j := strings.Index(text[i:], "\n")
			if j < 0 {
				i = n
			} else {
				i += j
			}
			continue
		}
		if ch == '/' && i+1 < n && text[i+1] == '*' {
			j := strings.Index(text[i+2:], "*/")
			if j < 0 {
				i = n
			} else {
				i = i + 2 + j + 2
			}
			continue
		}
		if strings.HasPrefix(text[i:], `"""`) {
			i = skipRaw(text, i)
			continue
		}
		if ch == '@' && i+1 < n && text[i+1] == '"' {
			i = skipVerbatim(text, i+1)
			continue
		}
		if ch == '"' {
			i = skipString(text, i)
			continue
		}
		if ch == '\'' {
			i = skipChar(text, i)
			continue
		}
		if ch == '{' {
			depth++
		} else if ch == '}' {
			depth--
			if depth == 0 {
				return text[start : i+1], true
			}
		}
		i++
	}
	return "", false
}

// blankComments blanks out the comments, keeping the length and the line
// structure.
//
// Blanking keeps every line in place, so a line number computed against the
// blanked text is the line number of the original file.
func blankComments(text string) string {
	var out strings.Builder
	i := 0
	n := len(text)
	for i < n {
		ch := text[i]
		if ch == '/' && i+1 < n && text[i+1] == '/' {
			j := strings.Index(text[i:], "\n")
			if j < 0 {
				out.WriteString(strings.Repeat(" ", n-i))
				i = n
				continue
			}
			out.WriteString(strings.Repeat(" ", j))
			i += j
			continue
		}
		if ch == '/' && i+1 < n && text[i+1] == '*' {
			j := strings.Index(text[i+2:], "*/")
			end := n
			if j >= 0 {
				end = i + 2 + j + 2
			}
			out.WriteString(blankOut(text[i:end]))
			i = end
			continue
		}
		if strings.HasPrefix(text[i:], `"""`) {
			j := skipRaw(text, i)
			out.WriteString(text[i:j])
			i = j
			continue
		}
		if ch == '@' && i+1 < n && text[i+1] == '"' {
			j := skipVerbatim(text, i+1)
			out.WriteString(text[i:j])
			i = j
			continue
		}
		if ch == '"' {
			j := skipString(text, i)
			out.WriteString(text[i:j])
			i = j
			continue
		}
		if ch == '\'' {
			j := skipChar(text, i)
			out.WriteString(text[i:j])
			i = j
			continue
		}
		out.WriteByte(ch)
		i++
	}
	return out.String()
}

// blankOut is a comment's own shape, with every non-newline character
// blanked.
func blankOut(text string) string {
	var out strings.Builder
	for _, r := range text {
		if r == '\n' {
			out.WriteByte('\n')
			continue
		}
		out.WriteByte(' ')
	}
	return out.String()
}
