using System.Text;
using Harbor.TestKit;
using TUnit.Core;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Robustness contract for the JSONL read hot path (#2704 P03): a line
///     that came from outside the process is CLASSIFIED, never allowed to
///     throw out of <see cref="JsonlLineParser.Parse" />. The parser is the
///     one the store's read path actually uses, so an exception here would
///     take down a whole session reload instead of skipping one bad row.
/// </summary>
/// <remarks>
///     Two shapes are generated. An invalid leading byte exercises the
///     outermost guard; a syntactically valid envelope whose payload is a
///     string instead of an object gets deeper into the role-specific codec.
///     Both are known-bad by construction, so the invariant can demand a
///     failure result rather than merely "no exception". The seed and failing
///     input come from <see cref="TestSeeds" /> and <see cref="FuzzLoop{TInput}" />.
/// </remarks>
public class JsonlLineParserFuzzTests
{
    private const int Cases = 200;

    // A byte MessagePack/JSON never uses as a token; deliberately not a UTF-8
    // lead byte, so the parser's first reader must reject it.
    private const byte InvalidLeadingByte = 0xC1;

    private const string JunkAlphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,:;-_/=+?!";

    [Test]
    public async Task RandomLines_AreClassifiedAsFailures_WithoutThrowing()
    {
        foreach (int seed in TestSeeds.Values(314159))
        {
            await FuzzLoop<byte[]>.RunAsync(
                "jsonl-line-parse", seed, Cases, MakeLine,
                (line, _) =>
                {
                    // If this throws, FuzzLoop wraps it with the seed and the
                    // exact bytes — that is the finding, not a silent skip.
                    var result = JsonlLineParser.Parse(line, "fuzz-session");

                    if (!result.IsFailure)
                    {
                        throw new InvalidOperationException(
                            "a known-malformed line was accepted as a valid message");
                    }

                    if (string.IsNullOrWhiteSpace(result.Error))
                    {
                        throw new InvalidOperationException(
                            "a rejected line carried no diagnostic for the read loop to log");
                    }

                    return Task.CompletedTask;
                });
        }
    }

    private static byte[] MakeLine(Random rng)
    {
        if (rng.Next(2) == 0)
        {
            byte[] raw = new byte[rng.Next(1, 257)];
            rng.NextBytes(raw);
            raw[0] = InvalidLeadingByte;
            return raw;
        }

        var line = new StringBuilder(
            "{\"type\":\"message\",\"id\":\"m1\",\"role\":\"assistant\",\"payload\":\"");
        int junkLength = rng.Next(0, 80);
        for (int i = 0; i < junkLength; i++)
        {
            line.Append(JunkAlphabet[rng.Next(JunkAlphabet.Length)]);
        }

        line.Append("\"}");
        return Encoding.UTF8.GetBytes(line.ToString());
    }
}
