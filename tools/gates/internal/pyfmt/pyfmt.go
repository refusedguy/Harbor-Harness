// Package pyfmt renders numbers the way python does.
//
// The gates this repository ports print numbers with python's formatting, and
// the workflows read those lines. Two things have to match: how a float is
// ROUNDED (python rounds to the nearest, ties to even, on the EXACT value of
// the double) and how it is PRINTED (python's repr: the shortest form that
// reads back as the same double, always with a decimal point or an
// exponent).
package pyfmt

import (
	"math"
	"strconv"
	"strings"
)

// Round rounds a float to n decimal places, the way python's round does.
//
// The ties go to even, and the rounding is done on the EXACT value of the
// double: round(2.675, 2) is 2.67 in python and in Go, because 2.675 is
// really 2.67499999999999982236431605997495353221893310546875.
func Round(value float64, n int) float64 {
	text := strconv.FormatFloat(value, 'f', n, 64)
	rounded, err := strconv.ParseFloat(text, 64)
	if err != nil {
		return value
	}
	return rounded
}

// Repr renders a float the way python's repr does.
//
//	5.23     -> "5.23"
//	5.0      -> "5.0"
//	1e+16    -> "1e+16"
//	1e15     -> "1000000000000000.0"
//	0.0001   -> "0.0001"
//	0.00001  -> "1e-05"
func Repr(value float64) string {
	switch {
	case math.IsNaN(value):
		return "nan"
	case math.IsInf(value, 1):
		return "inf"
	case math.IsInf(value, -1):
		return "-inf"
	}

	// The shortest digits, with the decimal exponent.
	scientific := strconv.FormatFloat(value, 'e', -1, 64)
	mantissa, exponentText, _ := strings.Cut(scientific, "e")
	exponent, err := strconv.Atoi(exponentText)
	if err != nil {
		return scientific
	}
	digits := strings.Replace(mantissa, ".", "", 1)

	// The decimal point sits after decpt digits.
	decpt := exponent + 1
	if decpt < -3 || decpt > 16 {
		return scientific
	}
	switch {
	case decpt <= 0:
		return "0." + strings.Repeat("0", -decpt) + digits
	case decpt >= len(digits):
		return digits + strings.Repeat("0", decpt-len(digits)) + ".0"
	default:
		return digits[:decpt] + "." + digits[decpt:]
	}
}
