package stringswitch

// fixture.go -- the python original's SELF_TEST_SOURCE, byte for byte.
//
// The fixture is the same one, so a port that changed the scanner's
// reading of it fails here.
const selfTestSource = `namespace Harbor.Sample;

public class Sample
{
    public string Dispatch(string kind)
    {
        switch (kind)
        {
            case "udp":
                return "a";
            case "tcp":
                return "b";
            default:
                return "c";
        }
    }

    public string ExpressionDispatch(string kind) =>
        kind switch
        {
            "udp" => "a",
            _ => "b",
        };

    public void Comment()
    {
        // case "not-a-switch": this is prose.
        /* case "also-not": prose too. */
    }

    public string RawString() => \"\"\"
        case "emitted": this is text, not code.
        \"\"\";

    public char CharDispatch(char c) => c switch
    {
        'a' => 'b',
        _ => c,
    };
}`
