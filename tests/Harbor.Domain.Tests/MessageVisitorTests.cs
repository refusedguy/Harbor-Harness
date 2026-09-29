using System.Reflection;
using Harbor.Abstractions.Models;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     #461 — the guard for the two sum-type traversals.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ContentPart" /> and <see cref="AgentMessage" /> are closed
///         unions that every consumer used to walk with its own hand-rolled
///         <c>switch</c>; most of those switches had no <c>default:</c> arm, so a
///         new subtype was dropped without a word. The single dispatch now lives in
///         <see cref="ContentPartVisitor{TResult}" /> /
///         <see cref="AgentMessageVisitor{TResult}" />.
///     </para>
///     <para>
///         These tests are the part of that fix a compiler cannot check for us:
///         adding a subtype to the union without giving the visitor an arm for it
///         fails <see cref="Every_ContentPart_Subtype_Has_A_Visitor_Arm" /> and
///         <see cref="Every_AgentMessage_Subtype_Has_A_Visitor_Arm" />, and a part
///         that reaches a walker without an arm is refused rather than dropped
///         (the <c>Refuses_*</c> tests).
///     </para>
/// </remarks>
public class MessageVisitorTests
{
    // ── ContentPart: every case of the union has an arm ─────────────────────

    [Test]
    public async Task Every_ContentPart_Subtype_Has_A_Visitor_Arm()
    {
        Type[] subtypes = DeclaredSubtypes(typeof(ContentPart));
        var problems = new List<string>();

        foreach (Type subtype in subtypes)
        {
            if (!HasVisitArm<ContentPartVisitor<string[]>>(subtype))
            {
                problems.Add($"ContentPartVisitor<TResult> declares no public Visit({subtype.Name}) arm.");
                continue;
            }

            try
            {
                string arm = new PartProbe().Accept((ContentPart)Sample(subtype));
                if (arm != subtype.Name)
                    problems.Add($"Accept routed {subtype.Name} to the {arm} arm.");
            }
            catch (NotSupportedException ex)
            {
                problems.Add($"Accept has no arm for {subtype.Name} and refuses it: {ex.Message}");
            }
        }

        await Assert.That(subtypes.Length).IsGreaterThan(0);
        await Assert.That(problems.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task ContentPart_Union_Is_Exactly_The_Four_Armed_Kinds()
    {
        string[] names = DeclaredSubtypes(typeof(ContentPart))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Adding a fifth subtype must update this list in the same commit: the
        // point is that the union change is a reviewed decision, not a side effect.
        await Assert.That(names).IsEquivalentTo(new[] { "FilePart", "TextPart", "ThinkingPart", "ToolCallPart" });
    }

    // ── ContentPart: an unknown kind is refused, not dropped ────────────────

    [Test]
    public async Task Accept_Refuses_A_Content_Part_Kind_The_Walker_Does_Not_Know()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new PartProbe().Accept(new RoguePart("payload")));

        await Assert.That(ex.Message.Contains("RoguePart", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task A_Walker_May_Report_An_Unknown_Kind_Instead_Of_Refusing_It()
    {
        string[] seen = new ReportingProbe().Accept(new RoguePart("payload"));

        await Assert.That(seen).IsEquivalentTo(new[] { "RoguePart" });
    }

    [Test]
    public async Task Walk_Visits_Every_Part_In_Order_And_Returns_The_Last_Arm()
    {
        ContentPart[] parts =
        [
            new TextPart("a"),
            new ThinkingPart("b"),
            new ToolCallPart("id", "read", default),
            new FilePart("p", "image/png", 12),
        ];

        string last = new PartProbe().Walk(parts);

        await Assert.That(last).IsEqualTo("FilePart");
    }

    // ── AgentMessage: same two guarantees ───────────────────────────────────

    [Test]
    public async Task Every_AgentMessage_Subtype_Has_A_Visitor_Arm()
    {
        Type[] subtypes = DeclaredSubtypes(typeof(AgentMessage));
        var problems = new List<string>();

        foreach (Type subtype in subtypes)
        {
            if (!HasVisitArm<AgentMessageVisitor<string[]>>(subtype))
            {
                problems.Add($"AgentMessageVisitor<TResult> declares no public Visit({subtype.Name}) arm.");
                continue;
            }

            try
            {
                string arm = new MessageProbe().Accept((AgentMessage)Sample(subtype));
                if (arm != subtype.Name)
                    problems.Add($"Accept routed {subtype.Name} to the {arm} arm.");
            }
            catch (NotSupportedException ex)
            {
                problems.Add($"Accept has no arm for {subtype.Name} and refuses it: {ex.Message}");
            }
        }

        await Assert.That(subtypes.Length).IsGreaterThan(0);
        await Assert.That(problems.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Accept_Refuses_A_Message_Role_The_Walker_Does_Not_Know()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new MessageProbe().Accept(new RogueMessage("id", "s", DateTimeOffset.UnixEpoch)));

        await Assert.That(ex.Message.Contains("RogueMessage", StringComparison.Ordinal)).IsTrue();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     The concrete cases of a union, taken from the assembly that declares it
    ///     — deliberately not from every loaded assembly, so the test-only
    ///     <see cref="RoguePart" /> below cannot poison the enumeration.
    /// </summary>
    private static Type[] DeclaredSubtypes(Type baseType) =>
        baseType.Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && baseType.IsAssignableFrom(t))
            .ToArray();

    private static bool HasVisitArm<TVisitor>(Type caseType)
    {
        foreach (MethodInfo method in typeof(TVisitor).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!string.Equals(method.Name, "Visit", StringComparison.Ordinal))
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == caseType)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Builds a throwaway instance of a union case. Only the arms matter to
    ///     these tests, so every string parameter gets a placeholder, every value
    ///     type its default, and every reference-typed parameter null.
    /// </summary>
    private static object Sample(Type caseType)
    {
        ConstructorInfo ctor = caseType.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .First();

        ParameterInfo[] parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            args[i] = parameterType == typeof(string)
                ? "sample"
                : parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
        }

        return ctor.Invoke(args);
    }

    /// <summary>A walker that names the arm each part landed in.</summary>
    private sealed class PartProbe : ContentPartVisitor<string>
    {
        public override string Visit(TextPart part) => nameof(TextPart);

        public override string Visit(ThinkingPart part) => nameof(ThinkingPart);

        public override string Visit(ToolCallPart part) => nameof(ToolCallPart);

        public override string Visit(FilePart part) => nameof(FilePart);
    }

    /// <summary>A walker that reports an unknown kind instead of refusing it.</summary>
    private sealed class ReportingProbe : ContentPartVisitor<string[]>
    {
        public override string[] Visit(TextPart part) => new[] { nameof(TextPart) };

        public override string[] Visit(ThinkingPart part) => new[] { nameof(ThinkingPart) };

        public override string[] Visit(ToolCallPart part) => new[] { nameof(ToolCallPart) };

        public override string[] Visit(FilePart part) => new[] { nameof(FilePart) };

        protected override string[] VisitUnknown(ContentPart part) => new[] { part.GetType().Name };
    }

    private sealed class MessageProbe : AgentMessageVisitor<string>
    {
        public override string Visit(UserMessage message) => nameof(UserMessage);

        public override string Visit(AssistantMessage message) => nameof(AssistantMessage);

        public override string Visit(ToolResultMessage message) => nameof(ToolResultMessage);
    }

    /// <summary>
    ///     A part kind the union does not know — the shape a future
    ///     <see cref="ContentPart" /> subtype would have if someone added it
    ///     without teaching the visitor.
    /// </summary>
    private sealed record RoguePart(string Text) : ContentPart
    {
        public override string Type => "rogue";
    }

    private sealed record RogueMessage(string Id, string SessionId, DateTimeOffset CreatedAt)
        : AgentMessage(Id, SessionId, CreatedAt)
    {
        public override string Role => "rogue";
    }
}
