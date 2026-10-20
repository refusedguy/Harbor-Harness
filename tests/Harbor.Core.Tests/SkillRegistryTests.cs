using Harbor.Abstractions.Sessions;
using Harbor.Application.Skills;

namespace Harbor.Core.Tests;

/// <summary>
///     Tests for <see cref="SkillRegistry" /> and <see cref="SkillPolicyMatrix" />:
///     the merge rule across providers, the invocation policy, and the two
///     answers an empty registry and an absent policy have to give.
/// </summary>
/// <remarks>
///     The registry is the answer to "which skills exist" and "who may call
///     them". These tests pin both halves: the merge is by the provider's
///     DECLARED rank (never by registration order), and the policy is data —
///     per skill, and per request context through the matrix.
/// </remarks>
public class SkillRegistryTests
{
    private const string WorkDir = "/tmp/harbor-skill-registry-tests";

    // ── the merge rule ─────────────────────────────────────────────────────────

    [Test]
    public async Task GetSkills_ReturnsEveryRegisteredSkill_SortedByName()
    {
        var registry = new SkillRegistry(
        [
            new FakeProvider("beta", 10, [Skill("alpha"), Skill("gamma")]),
            new FakeProvider("other", 20, [Skill("beta")]),
        ]);

        IReadOnlyList<SkillDescriptor> skills = registry.GetSkills(WorkDir, SkillCaller.Model);

        await Assert.That(skills.Count).IsEqualTo(3);
        await Assert.That(skills[0].Name).IsEqualTo("alpha");
        await Assert.That(skills[1].Name).IsEqualTo("beta");
        await Assert.That(skills[2].Name).IsEqualTo("gamma");
    }

    [Test]
    public async Task GetSkills_HigherPriorityProviderWinsOnTheSameName()
    {
        var registry = new SkillRegistry(
        [
            new FakeProvider("low", 10, [Skill("deploy", "from the low-ranked provider")]),
            new FakeProvider("high", 100, [Skill("deploy", "from the high-ranked provider")]),
        ]);

        IReadOnlyList<SkillDescriptor> skills = registry.GetSkills(WorkDir, SkillCaller.Model);

        await Assert.That(skills.Count).IsEqualTo(1);
        await Assert.That(skills[0].Description).IsEqualTo("from the high-ranked provider");
    }

    [Test]
    public async Task GetSkills_EqualPriority_IsBrokenByTheProviderName_NotByRegistrationOrder()
    {
        var aaa = new FakeProvider("aaa", 50, [Skill("deploy", "from aaa")]);
        var bbb = new FakeProvider("bbb", 50, [Skill("deploy", "from bbb")]);

        var forward = new SkillRegistry([aaa, bbb]);
        var backward = new SkillRegistry([bbb, aaa]);

        await Assert.That(forward.GetSkills(WorkDir, SkillCaller.Model)[0].Description).IsEqualTo("from aaa");
        await Assert.That(backward.GetSkills(WorkDir, SkillCaller.Model)[0].Description).IsEqualTo("from aaa");
    }

    [Test]
    public async Task GetSkills_MatchesTheNameCaseInsensitively()
    {
        var registry = new SkillRegistry([new FakeProvider("p", 10, [Skill("deploy")])]);

        await Assert.That(registry.GetSkills(WorkDir, SkillCaller.Model).Count).IsEqualTo(1);
        await Assert.That(registry.ResolveForCall(WorkDir, "DEPLOY", SkillCaller.Model).IsSuccess).IsTrue();
    }

    // ── the invocation policy, per skill ───────────────────────────────────────

    [Test]
    public async Task GetSkills_PolicyKeepsTheSkillFromTheModelAndKeepsItForTheUser()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("slash-command", modelInvocable: false)])]);

        IReadOnlyList<SkillDescriptor> forModel = registry.GetSkills(WorkDir, SkillCaller.Model);
        IReadOnlyList<SkillDescriptor> forUser = registry.GetSkills(WorkDir, SkillCaller.User);

        await Assert.That(forModel.Count).IsEqualTo(0);
        await Assert.That(forUser.Count).IsEqualTo(1);
        await Assert.That(forUser[0].Name).IsEqualTo("slash-command");
    }

    [Test]
    public async Task ResolveForCall_RefusesAPolicyBlockedSkill_WithAReadableReason()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("slash-command", modelInvocable: false)])]);

        CSharpFunctionalExtensions.Result<SkillDescriptor> result =
            registry.ResolveForCall(WorkDir, "slash-command", SkillCaller.Model);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not invocable by the model");
        await Assert.That(result.Error).Contains("slash-command");
    }

    [Test]
    public async Task ResolveForCall_AllowsTheSameSkillForTheCallerThePolicyAdmits()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("slash-command", modelInvocable: false)])]);

        CSharpFunctionalExtensions.Result<SkillDescriptor> result =
            registry.ResolveForCall(WorkDir, "slash-command", SkillCaller.User);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Name).IsEqualTo("slash-command");
    }

    [Test]
    public async Task ResolveForCall_AnUnknownSkillFailsWithAReadableReason()
    {
        var registry = new SkillRegistry([new FakeProvider("p", 10, [Skill("deploy")])]);

        CSharpFunctionalExtensions.Result<SkillDescriptor> result =
            registry.ResolveForCall(WorkDir, "nope", SkillCaller.Model);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not available");
    }

    // ── the matrix: policy per request context ────────────────────────────────

    [Test]
    public async Task Matrix_NarrowsTheRequestItMatches()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("allowed"), Skill("other")])],
            new SkillPolicyMatrix([new SkillPolicyRule(SkillCaller.Model, "plan", null, new[] { "allowed" })]));

        IReadOnlyList<SkillDescriptor> inPlanMode = registry.GetSkills(WorkDir, SkillCaller.Model, "plan");

        await Assert.That(inPlanMode.Count).IsEqualTo(1);
        await Assert.That(inPlanMode[0].Name).IsEqualTo("allowed");
    }

    [Test]
    public async Task Matrix_DoesNotNarrowARequestNoRowMatches()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("allowed"), Skill("other")])],
            new SkillPolicyMatrix([new SkillPolicyRule(SkillCaller.Model, "plan", null, new[] { "allowed" })]));

        IReadOnlyList<SkillDescriptor> inCodeMode = registry.GetSkills(WorkDir, SkillCaller.Model, "code");
        IReadOnlyList<SkillDescriptor> forTheUser = registry.GetSkills(WorkDir, SkillCaller.User, "plan");

        await Assert.That(inCodeMode.Count).IsEqualTo(2);
        await Assert.That(forTheUser.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Matrix_NeverWidensASkillTheProviderKeptFromTheCaller()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("kept-away", modelInvocable: false)])],
            new SkillPolicyMatrix([new SkillPolicyRule(SkillCaller.Model, null, null, ["kept-away"])]));

        IReadOnlyList<SkillDescriptor> skills = registry.GetSkills(WorkDir, SkillCaller.Model, "plan");

        await Assert.That(skills.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Matrix_RefusesACallItExcludes_WithAReadableReason()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("allowed"), Skill("excluded")])],
            new SkillPolicyMatrix([new SkillPolicyRule(SkillCaller.Model, "plan", null, new[] { "allowed" })]));

        CSharpFunctionalExtensions.Result<SkillDescriptor> result =
            registry.ResolveForCall(WorkDir, "excluded", SkillCaller.Model, "plan");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("invocation policy");
    }

    [Test]
    public async Task EmptyMatrix_AnswersWithTheSkillsOwnPolicyAlone()
    {
        var registry = new SkillRegistry(
            [new FakeProvider("p", 10, [Skill("a"), Skill("b", modelInvocable: false)])],
            SkillPolicyMatrix.Empty);

        IReadOnlyList<SkillDescriptor> forModel = registry.GetSkills(WorkDir, SkillCaller.Model);

        await Assert.That(forModel.Count).IsEqualTo(1);
        await Assert.That(forModel[0].Name).IsEqualTo("a");
    }

    // ── the empty registry and the absent policy ──────────────────────────────

    [Test]
    public async Task EmptyRegistry_AnswersPredictably()
    {
        var registry = new SkillRegistry([]);

        await Assert.That(registry.GetSkills(WorkDir, SkillCaller.Model).Count).IsEqualTo(0);
        await Assert.That(registry.GetSkills(WorkDir, SkillCaller.User).Count).IsEqualTo(0);

        CSharpFunctionalExtensions.Result<SkillDescriptor> result =
            registry.ResolveForCall(WorkDir, "anything", SkillCaller.Model);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not available");
    }

    [Test]
    public async Task NoPolicy_KeepsEverySkillTheProviderDeclared()
    {
        var registry = new SkillRegistry([new FakeProvider("p", 10, [Skill("a"), Skill("b"), Skill("c")])]);

        await Assert.That(registry.GetSkills(WorkDir, SkillCaller.Model).Count).IsEqualTo(3);
        await Assert.That(registry.GetSkills(WorkDir, SkillCaller.User).Count).IsEqualTo(3);
    }

    [Test]
    public async Task ResolveForCall_AnEmptyNameFails()
    {
        var registry = new SkillRegistry([new FakeProvider("p", 10, [Skill("a")])]);

        CSharpFunctionalExtensions.Result<SkillDescriptor> result =
            registry.ResolveForCall(WorkDir, "   ", SkillCaller.Model);

        await Assert.That(result.IsFailure).IsTrue();
    }

    // ── the workspace provider: the policy as the SKILL declares it ────────────

    [Test]
    public async Task WorkspaceProvider_ReadsTheDeclaredPolicyFromTheSkillFile()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"harbor-skill-policy-{Guid.NewGuid():N}");
        string skillsDir = Path.Combine(root, ".harbor", "skills", "policy-probe");
        Directory.CreateDirectory(skillsDir);

        try
        {
            File.WriteAllText(
                Path.Combine(skillsDir, "SKILL.md"),
                "---\nname: policy-probe\ndescription: A probe skill\nmodel-invocable: false\n---\n# Probe body\n");

            var registry = new SkillRegistry([new SkillProvider()]);

            IReadOnlyList<SkillDescriptor> forModel = registry.GetSkills(root, SkillCaller.Model);
            IReadOnlyList<SkillDescriptor> forUser = registry.GetSkills(root, SkillCaller.User);

            SkillDescriptor? modelCopy = null;
            foreach (SkillDescriptor skill in forModel)
            {
                if (skill.Name == "policy-probe")
                {
                    modelCopy = skill;
                }
            }

            SkillDescriptor? userCopy = null;
            foreach (SkillDescriptor skill in forUser)
            {
                if (skill.Name == "policy-probe")
                {
                    userCopy = skill;
                }
            }

            await Assert.That(modelCopy).IsNull()
                .Because("the skill file declares model-invocable: false, so the model must not be offered it");
            await Assert.That(userCopy).IsNotNull()
                .Because("the skill file says nothing about the human, and the default admits both");
            await Assert.That(userCopy!.UserInvocable).IsTrue();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SkillDescriptor Skill(
        string name,
        string description = "a skill",
        bool modelInvocable = true,
        bool userInvocable = true)
        => new(name, description, $"/skills/{name}.md")
        {
            ModelInvocable = modelInvocable,
            UserInvocable = userInvocable,
        };

    /// <summary>A provider that answers with a fixed catalog.</summary>
    private sealed class FakeProvider(
        string name,
        int priority,
        IReadOnlyList<SkillDescriptor> skills) : ISkillProvider
    {
        public string Name { get; } = name;

        public int Priority { get; } = priority;

        public IReadOnlyList<SkillDescriptor> GetSkills(string workingDirectory) => skills;

        public string? ReadSkill(string workingDirectory, string name) => null;
    }
}
