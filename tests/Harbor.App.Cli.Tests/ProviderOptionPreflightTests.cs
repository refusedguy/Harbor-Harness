using CSharpFunctionalExtensions;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2686 Q07. Option validation is pure: these tests pin the descriptors a
///     model's capability facts produce and the rejections a caller sees before
///     a turn starts, without a provider or a network call.
/// </summary>
public class ProviderOptionPreflightTests
{
    private static readonly ModelOptionProfile Capable =
        new(SupportsToolUse: true, SupportsVision: true, SupportsReasoning: true, MaxOutputTokens: 4096, ContextWindow: 128000);

    private static readonly ModelOptionProfile Plain =
        new(SupportsToolUse: false, SupportsVision: false, SupportsReasoning: false, MaxOutputTokens: 512, ContextWindow: 8000);

    [Test]
    public async Task DescriptorsFor_ReflectTheCapabilityFacts()
    {
        IReadOnlyList<OptionDescriptor> capable = ProviderOptionPreflight.DescriptorsFor(Capable);
        IReadOnlyList<OptionDescriptor> plain = ProviderOptionPreflight.DescriptorsFor(Plain);

        await Assert.That(Find(capable, "toolUse")!.Allowed.Count).IsEqualTo(2);
        await Assert.That(Find(plain, "toolUse")!.Allowed.Count).IsEqualTo(1);
        await Assert.That(Find(plain, "toolUse")!.Allowed[0]).IsEqualTo("false");
        await Assert.That(Find(plain, "maxOutputTokens")!.Maximum).IsEqualTo(512);
        await Assert.That(Find(plain, "reasoning")).IsNotNull();
    }

    [Test]
    public async Task Validate_AcceptsEverySupportedOption()
    {
        var options = new[]
        {
            new OptionAssignment("toolUse", "true"),
            new OptionAssignment("vision", "false"),
            new OptionAssignment("reasoning", "true"),
            new OptionAssignment("temperature", "0.7"),
            new OptionAssignment("maxOutputTokens", "1024"),
        };

        Result outcome = ProviderOptionPreflight.Validate(Capable, options);

        await Assert.That(outcome.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Validate_RejectsAChoiceTheModelCannotHonour()
    {
        Result outcome = ProviderOptionPreflight.Validate(
            Plain,
            [new OptionAssignment("toolUse", "true")]);

        await Assert.That(outcome.IsFailure).IsTrue();
        await Assert.That(outcome.Error).Contains("toolUse");
    }

    [Test]
    public async Task Validate_RejectsUnknownOptionsAndOutOfRangeNumbers()
    {
        Result unknown = ProviderOptionPreflight.Validate(Capable, [new OptionAssignment("mystery", "1")]);
        Result low = ProviderOptionPreflight.Validate(Capable, [new OptionAssignment("temperature", "-0.1")]);
        Result high = ProviderOptionPreflight.Validate(Capable, [new OptionAssignment("maxOutputTokens", "9999")]);
        Result text = ProviderOptionPreflight.Validate(Capable, [new OptionAssignment("topP", "wide")]);

        await Assert.That(unknown.IsFailure).IsTrue();
        await Assert.That(low.IsFailure).IsTrue();
        await Assert.That(high.IsFailure).IsTrue();
        await Assert.That(text.IsFailure).IsTrue();
    }

    [Test]
    public async Task Validate_RejectsDuplicatesAndTooManyOptions()
    {
        Result duplicate = ProviderOptionPreflight.Validate(
            Capable,
            [new OptionAssignment("temperature", "0.2"), new OptionAssignment("temperature", "0.3")]);

        var many = new List<OptionAssignment>();
        for (int i = 0; i < ProviderOptionPreflight.MaxOptions + 1; i++)
            many.Add(new OptionAssignment("toolUse", "true"));

        Result capped = ProviderOptionPreflight.Validate(Capable, many);

        await Assert.That(duplicate.IsFailure).IsTrue();
        await Assert.That(duplicate.Error).Contains("more than once");
        await Assert.That(capped.IsFailure).IsTrue();
    }

    [Test]
    public async Task ParseOptions_ReadsProviderModelAndAssignments()
    {
        Result<PreflightRequest> parsed = ProviderOptionPreflight.ParseOptions(
            ["kilocode", "tencent/hy3:free", "--option", "toolUse=true", "--option", "temperature=0.4"]);

        Result<PreflightRequest> missing = ProviderOptionPreflight.ParseOptions([]);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.ProviderId).IsEqualTo("kilocode");
        await Assert.That(parsed.Value.ModelId).IsEqualTo("tencent/hy3:free");
        await Assert.That(parsed.Value.Options.Count).IsEqualTo(2);
        await Assert.That(parsed.Value.Options[1].Name).IsEqualTo("temperature");
        await Assert.That(parsed.Value.Options[1].Value).IsEqualTo("0.4");
        await Assert.That(missing.IsFailure).IsTrue();
    }

    [Test]
    public async Task FormatResult_ShapesTheVerdictAsJson()
    {
        string good = ProviderOptionPreflight.FormatResult("kilocode", "m1", Result.Success());
        string bad = ProviderOptionPreflight.FormatResult(
            "kilocode", "m1", Result.Failure("option 'x' is not supported."));

        await Assert.That(good).IsEqualTo("{\"provider\":\"kilocode\",\"model\":\"m1\",\"valid\":true,\"errors\":[]}");
        await Assert.That(bad).Contains("\"valid\":false");
        await Assert.That(bad).Contains("not supported");
    }

    private static OptionDescriptor? Find(IReadOnlyList<OptionDescriptor> descriptors, string name)
    {
        foreach (OptionDescriptor descriptor in descriptors)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.Ordinal)) return descriptor;
        }

        return null;
    }
}
