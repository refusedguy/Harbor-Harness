using System.Text.Json;
using Harbor.Abstractions.Tools;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     <see cref="JsonArgValidator" /> — the shared ValidateArguments primitives (#181).
///     Every assertion pins the exact failure message: tools delegate to these helpers
///     precisely so the public error contract stays byte-identical.
/// </summary>
public class JsonArgValidatorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── RequiredString ──

    [Test]
    public async Task RequiredString_AcceptsValue()
    {
        var args = Parse("""{"path":"a.txt"}""");

        var ok = JsonArgValidator.RequiredString(args, "path", "Missing or empty 'path'.");
        await Assert.That(ok.IsSuccess).IsTrue();
        await Assert.That(ok.Value).IsEqualTo("a.txt");
    }

    [Test]
    public async Task RequiredString_RejectsMissingWrongKindAndBlank()
    {
        const string message = "Missing or empty 'path'.";

        await Assert.That(JsonArgValidator.RequiredString(Parse("{}"), "path", message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.RequiredString(Parse("""{"path":42}"""), "path", message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.RequiredString(Parse("""{"path":""}"""), "path", message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.RequiredString(Parse("""{"path":"   "}"""), "path", message).Error)
            .IsEqualTo(message);
    }

    // ── RequiredNonEmptyString (grep/ripgrep: whitespace-only passes) ──

    [Test]
    public async Task RequiredNonEmptyString_AllowsWhitespaceRejectsEmpty()
    {
        const string message = "Missing required argument 'pattern'.";

        var blank = JsonArgValidator.RequiredNonEmptyString(Parse("""{"pattern":"   "}"""), "pattern", message);
        await Assert.That(blank.IsSuccess).IsTrue();

        await Assert.That(JsonArgValidator.RequiredNonEmptyString(Parse("{}"), "pattern", message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.RequiredNonEmptyString(Parse("""{"pattern":""}"""), "pattern", message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.RequiredNonEmptyString(Parse("""{"pattern":7}"""), "pattern", message).Error)
            .IsEqualTo(message);
    }

    // ── RequiredStringPresent (write content: empty allowed) ──

    [Test]
    public async Task RequiredStringPresent_AllowsEmpty()
    {
        const string message = "Missing required argument 'content'.";

        var empty = JsonArgValidator.RequiredStringPresent(Parse("""{"content":""}"""), "content", message);
        await Assert.That(empty.IsSuccess).IsTrue();
        await Assert.That(empty.Value).IsEqualTo(string.Empty);

        await Assert.That(JsonArgValidator.RequiredStringPresent(Parse("{}"), "content", message).Error)
            .IsEqualTo(message);
    }

    // ── RequiredNonBlankString (bash: split missing vs blank message) ──

    [Test]
    public async Task RequiredNonBlankString_SplitsMessages()
    {
        var missing = JsonArgValidator.RequiredNonBlankString(
            Parse("{}"), "command", "Missing required argument 'command'.", "'command' cannot be empty.");
        await Assert.That(missing.Error).IsEqualTo("Missing required argument 'command'.");

        var wrongKind = JsonArgValidator.RequiredNonBlankString(
            Parse("""{"command":5}"""), "command", "Missing required argument 'command'.", "'command' cannot be empty.");
        await Assert.That(wrongKind.Error).IsEqualTo("Missing required argument 'command'.");

        var blank = JsonArgValidator.RequiredNonBlankString(
            Parse("""{"command":"  "}"""), "command", "Missing required argument 'command'.", "'command' cannot be empty.");
        await Assert.That(blank.Error).IsEqualTo("'command' cannot be empty.");

        var ok = JsonArgValidator.RequiredNonBlankString(
            Parse("""{"command":"echo hi"}"""), "command", "Missing required argument 'command'.", "'command' cannot be empty.");
        await Assert.That(ok.Value).IsEqualTo("echo hi");
    }

    // ── RequiredPath ──

    [Test]
    public async Task RequiredPath_UsesConventionalMessage()
    {
        var missing = JsonArgValidator.RequiredPath(Parse("{}"));
        await Assert.That(missing.IsFailure).IsTrue();
        await Assert.That(missing.Error).IsEqualTo("Missing or empty 'path'.");

        var ok = JsonArgValidator.RequiredPath(Parse("""{"path":"f"}"""));
        await Assert.That(ok.IsSuccess).IsTrue();
    }

    // ── RequiredInt / RequiredBool ──

    [Test]
    public async Task RequiredInt_AcceptsOnlyInt32()
    {
        var ok = JsonArgValidator.RequiredInt(Parse("""{"n":3}"""), "n", "Missing 'n'.");
        await Assert.That(ok.Value).IsEqualTo(3);

        await Assert.That(JsonArgValidator.RequiredInt(Parse("{}"), "n", "Missing 'n'.").IsFailure).IsTrue();
        await Assert.That(JsonArgValidator.RequiredInt(Parse("""{"n":"3"}"""), "n", "Missing 'n'.").IsFailure).IsTrue();
    }

    [Test]
    public async Task RequiredBool_AcceptsOnlyBooleans()
    {
        var ok = JsonArgValidator.RequiredBool(Parse("""{"b":true}"""), "b", "Missing 'b'.");
        await Assert.That(ok.Value).IsTrue();

        await Assert.That(JsonArgValidator.RequiredBool(Parse("{}"), "b", "Missing 'b'.").IsFailure).IsTrue();
        await Assert.That(JsonArgValidator.RequiredBool(Parse("""{"b":"true"}"""), "b", "Missing 'b'.").IsFailure).IsTrue();
    }

    // ── RequiredEnum / OptionalEnum ──

    [Test]
    public async Task RequiredEnum_MatchesSetAndEchoesUnknown()
    {
        string[] valid = ["diagnostics", "definition", "references"];

        var ok = JsonArgValidator.RequiredEnum(
            Parse("""{"action":"definition"}"""), "action", "Missing 'action'.",
            valid, static a => $"Unknown action '{a}'.");
        await Assert.That(ok.Value).IsEqualTo("definition");

        var unknown = JsonArgValidator.RequiredEnum(
            Parse("""{"action":"bogus"}"""), "action", "Missing 'action'.",
            valid, static a => $"Unknown action '{a}'.");
        await Assert.That(unknown.Error).IsEqualTo("Unknown action 'bogus'.");

        var missing = JsonArgValidator.RequiredEnum(
            Parse("{}"), "action", "Missing 'action'.",
            valid, static a => $"Unknown action '{a}'.");
        await Assert.That(missing.Error).IsEqualTo("Missing 'action'.");
    }

    [Test]
    public async Task RequiredEnum_HonorsComparison()
    {
        string[] valid = ["get", "set"];

        var ok = JsonArgValidator.RequiredEnum(
            Parse("""{"action":"GET"}"""), "action", "Missing 'action'.",
            valid, static a => $"Unknown '{a}'.", StringComparison.OrdinalIgnoreCase);
        await Assert.That(ok.Value).IsEqualTo("GET");

        var strict = JsonArgValidator.RequiredEnum(
            Parse("""{"action":"GET"}"""), "action", "Missing 'action'.",
            valid, static a => $"Unknown '{a}'.");
        await Assert.That(strict.Error).IsEqualTo("Unknown 'GET'.");
    }

    [Test]
    public async Task OptionalEnum_LenientOnAbsentAndWrongKind()
    {
        string[] valid = ["project", "global", "any"];
        const string message = "'scope' must be 'project', 'global' or 'any'.";

        await Assert.That(JsonArgValidator.OptionalEnum(Parse("{}"), "scope", valid, message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalEnum(Parse("""{"scope":5}"""), "scope", valid, message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalEnum(
            Parse("""{"scope":"PROJECT"}"""), "scope", valid, message, StringComparison.OrdinalIgnoreCase).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalEnum(
            Parse("""{"scope":"bogus"}"""), "scope", valid, message).Error).IsEqualTo(message);
    }

    // ── OptionalIntAtLeast / OptionalIntInRange (lenient on wrong types) ──

    [Test]
    public async Task OptionalIntAtLeast_FiresOnlyOnLowNumbers()
    {
        await Assert.That(JsonArgValidator.OptionalIntAtLeast(Parse("{}"), "offset", 1, "'offset' must be >= 1.").IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalIntAtLeast(Parse("""{"offset":"x"}"""), "offset", 1, "'offset' must be >= 1.").IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalIntAtLeast(Parse("""{"offset":2}"""), "offset", 1, "'offset' must be >= 1.").IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalIntAtLeast(Parse("""{"offset":0}"""), "offset", 1, "'offset' must be >= 1.").Error)
            .IsEqualTo("'offset' must be >= 1.");
    }

    [Test]
    public async Task OptionalIntInRange_ChecksBothBounds()
    {
        const string message = "'maxDepth' must be between 1 and 10.";

        await Assert.That(JsonArgValidator.OptionalIntInRange(Parse("""{"maxDepth":3}"""), "maxDepth", 1, 10, message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalIntInRange(Parse("""{"maxDepth":0}"""), "maxDepth", 1, 10, message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.OptionalIntInRange(Parse("""{"maxDepth":11}"""), "maxDepth", 1, 10, message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.OptionalIntInRange(Parse("""{"maxDepth":"deep"}"""), "maxDepth", 1, 10, message).IsSuccess).IsTrue();
    }

    // ── OptionalObject / OptionalBool / RequiredNumber / OptionalNumber ──

    [Test]
    public async Task OptionalObject_RejectsNonObjects()
    {
        const string message = "'args' must be a JSON object if present.";

        await Assert.That(JsonArgValidator.OptionalObject(Parse("{}"), "args", message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalObject(Parse("""{"args":{}}"""), "args", message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalObject(Parse("""{"args":[]}"""), "args", message).Error)
            .IsEqualTo(message);
    }

    [Test]
    public async Task OptionalBool_RejectsNonBooleans()
    {
        const string message = "Optional argument 'background' must be a boolean.";

        await Assert.That(JsonArgValidator.OptionalBool(Parse("{}"), "background", message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalBool(Parse("""{"background":false}"""), "background", message).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalBool(Parse("""{"background":"yes"}"""), "background", message).Error)
            .IsEqualTo(message);
        await Assert.That(JsonArgValidator.OptionalBool(Parse("""{"background":null}"""), "background", message).Error)
            .IsEqualTo(message);
    }

    [Test]
    public async Task RequiredNumber_And_OptionalNumber()
    {
        const string required = "'line' (1-based) is required.";
        const string optional = "'column' must be an integer.";

        await Assert.That(JsonArgValidator.RequiredNumber(Parse("""{"line":3}"""), "line", required).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.RequiredNumber(Parse("{}"), "line", required).Error).IsEqualTo(required);
        await Assert.That(JsonArgValidator.RequiredNumber(Parse("""{"line":"3"}"""), "line", required).Error).IsEqualTo(required);

        await Assert.That(JsonArgValidator.OptionalNumber(Parse("{}"), "column", optional).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalNumber(Parse("""{"column":2}"""), "column", optional).IsSuccess).IsTrue();
        await Assert.That(JsonArgValidator.OptionalNumber(Parse("""{"column":"2"}"""), "column", optional).Error).IsEqualTo(optional);
    }

    // ── HasString / HasNonEmptyArray ──

    [Test]
    public async Task PresencePredicates()
    {
        var args = Parse("""{"a":"x","b":"","c":[],"d":[1],"e":5}""");

        await Assert.That(JsonArgValidator.HasString(args, "a")).IsTrue();
        await Assert.That(JsonArgValidator.HasString(args, "b")).IsTrue();
        await Assert.That(JsonArgValidator.HasString(args, "e")).IsFalse();
        await Assert.That(JsonArgValidator.HasString(args, "missing")).IsFalse();

        await Assert.That(JsonArgValidator.HasNonEmptyArray(args, "d")).IsTrue();
        await Assert.That(JsonArgValidator.HasNonEmptyArray(args, "c")).IsFalse();
        await Assert.That(JsonArgValidator.HasNonEmptyArray(args, "a")).IsFalse();
        await Assert.That(JsonArgValidator.HasNonEmptyArray(args, "missing")).IsFalse();
    }
}
