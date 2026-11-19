using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.App.Cli.Commands;
using Harbor.Tools.Builtin;
using FakeProviderRegistryBase = Harbor.TestKit.Generated.FakeProviderRegistry;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #3793. <c>models preflight</c> resolves the target model through the shared
///     <see cref="ModelCatalogSearch" /> primitive. These tests pin the observable
///     behavior of <see cref="ProviderOptionRunner.RunAsync" />: a found model,
///     case-insensitive identity, fail-closed on unknown models, the strict
///     exact-id rule, and the bounded page size. The pure validator is untouched.
/// </summary>
// The runner writes to Console.Out/Error, so these tests must not race with
// other console-capturing suites in the same process.
[NotInParallel]
public class ProviderOptionRunnerSearchTests
{
    private static ModelInfo Model(string id, string provider, string display = "Model") =>
        new(id, provider, display, 128000, 4096, true, true, true, Pricing.Unknown, "openai");

    private static async Task<(int Exit, string Out, string Err)> RunAsync(
        IReadOnlyList<ModelInfo> models,
        params string[] args)
    {
        var registry = new FakeProviderRegistryBase
        {
            GetAllModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>(models),
        };
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            var swOut = new StringWriter();
            var swErr = new StringWriter();
            Console.SetOut(swOut);
            Console.SetError(swErr);
            int exit = await ProviderOptionRunner.RunAsync(args, CancellationToken.None, registry)
                .ConfigureAwait(false);
            return (exit, swOut.ToString(), swErr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Test]
    public async Task Preflight_FindsExistingModel()
    {
        ModelInfo[] models = [Model("tencent/hy3:free", "kilocode", "Hy3 Free")];

        var (exit, output, error) = await RunAsync(models, "kilocode", "tencent/hy3:free");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).IsEqualTo(
            "{\"provider\":\"kilocode\",\"model\":\"tencent/hy3:free\",\"valid\":true,\"errors\":[]}"
            + Environment.NewLine);
    }

    [Test]
    public async Task Preflight_ProviderAndModelIdAreCaseInsensitive()
    {
        ModelInfo[] models = [Model("tencent/hy3:free", "kilocode", "Hy3 Free")];

        var (exit, output, error) = await RunAsync(models, "KILOCODE", "TENCENT/HY3:FREE");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).Contains("\"valid\":true");
        // FormatResult keeps echoing the raw request values byte-for-byte.
        await Assert.That(output).Contains("\"provider\":\"KILOCODE\"");
    }

    [Test]
    public async Task Preflight_UnknownModel_FailsClosed()
    {
        ModelInfo[] models = [Model("tencent/hy3:free", "kilocode")];

        var (exit, output, error) = await RunAsync(models, "kilocode", "no-such-model");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).IsEmpty();
        await Assert.That(error).Contains(
            "models preflight: no model 'no-such-model' for provider 'kilocode'.");
    }

    [Test]
    public async Task Preflight_PartialIdMatchIsNotAccepted()
    {
        // The shared search matches by substring; preflight must keep the old
        // strict equality so a prefix never resolves a different model.
        ModelInfo[] models = [Model("tencent/hy3:free", "kilocode")];

        var (exit, output, error) = await RunAsync(models, "kilocode", "hy3");

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).IsEmpty();
        await Assert.That(error).Contains("no model 'hy3'");
    }

    [Test]
    public async Task Preflight_SearchLimitIsBoundedByMaxLimit()
    {
        // The consumer asks for DefaultLimit; the normalized query must stay
        // within the primitive's hard cap.
        await Assert.That(ModelCatalogSearch.TryCreate(
            "target", "kilocode", ModelCatalogSearch.DefaultLimit, 0, out var query)).IsTrue();
        await Assert.That(query.Limit).IsLessThanOrEqualTo(ModelCatalogSearch.MaxLimit);

        var models = new List<ModelInfo>();
        for (int i = 0; i < ModelCatalogSearch.MaxLimit + 5; i++)
            models.Add(Model($"model-{i}", "kilocode"));
        models[0] = Model("target", "kilocode");

        var (exit, output, error) = await RunAsync(models, "kilocode", "target");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(output).Contains("\"valid\":true");
    }
}
