using Harbor.Abstractions.Models.Identifiers;
namespace Harbor.Abstractions.Tests;
public class IdentifiersTests
{
    [Test]
    public async Task SessionId_Create_Valid_Value()
    {
        var id = SessionId.Create("abc123");
        await Assert.That(id.Value).IsEqualTo("abc123");
    }

    [Test]
    public async Task SessionId_Create_Empty_Throws()
    {
        try
        {
            _ = SessionId.Create("");
            Assert.Fail("Should have thrown ArgumentException");
        }
        catch (ArgumentException)
        { /* expected */
        }
    }

    [Test]
    public async Task SessionId_New_Generates_NonEmpty()
    {
        var id = SessionId.New();
        await Assert.That(string.IsNullOrEmpty(id.Value)).IsFalse();
    }

    [Test]
    public async Task SessionId_TryCreate_Empty_ReturnsFailure()
    {
        var result = SessionId.TryCreate("");
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task SessionId_Equality()
    {
        var a = SessionId.Create("test");
        var b = SessionId.Create("test");
        var c = SessionId.Create("other");
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
    }

    [Test]
    public async Task ProviderId_Normalizes_ToLowercase()
    {
        var id = ProviderId.Create("OpenAI");
        await Assert.That(id.Value).IsEqualTo("openai");
    }

    [Test]
    public async Task ProviderId_Rejects_InvalidCharacters()
    {
        try
        {
            _ = ProviderId.Create("open ai");
            Assert.Fail("Should throw");
        }
        catch (ArgumentException)
        { /* ok */
        }
        try
        {
            _ = ProviderId.Create("open.ai");
            Assert.Fail("Should throw");
        }
        catch (ArgumentException)
        { /* ok */
        }
    }

    [Test]
    public async Task ModelRef_TryParse_Valid()
    {
        var result = ModelRef.TryParse("anthropic/claude-opus-4");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId.Value).IsEqualTo("anthropic");
        await Assert.That(result.Value.ModelId).IsEqualTo("claude-opus-4");
    }

    [Test]
    public async Task ModelRef_TryParse_NoSlash_ReturnsFailure()
    {
        var result = ModelRef.TryParse("invalid");
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task ModelRef_ToString_CombinesProviderAndModel()
    {
        var ref_ = ModelRef.Create(ProviderId.Create("openai"), "gpt-4o");
        await Assert.That(ref_.ToString()).IsEqualTo("openai/gpt-4o");
    }

    [Test]
    public async Task ToolName_Normalizes_ToLowercase()
    {
        var name = ToolName.Create("Read");
        await Assert.That(name.Value).IsEqualTo("read");
    }

    [Test]
    public async Task ToolName_Rejects_InvalidPattern()
    {
        try
        {
            _ = ToolName.Create("read-file");
            Assert.Fail("Should throw");
        }
        catch (ArgumentException)
        { /* ok */
        }
        try
        {
            _ = ToolName.Create("123read");
            Assert.Fail("Should throw");
        }
        catch (ArgumentException)
        { /* ok */
        }
    }

    [Test]
    public async Task ToolName_Accepts_ValidPatterns()
    {
        await Assert.That(ToolName.Create("read").Value).IsEqualTo("read");
        await Assert.That(ToolName.Create("read_file").Value).IsEqualTo("read_file");
        await Assert.That(ToolName.Create("bash").Value).IsEqualTo("bash");
    }

    [Test]
    public async Task AgentName_Normalizes_ToLowercase()
    {
        var name = AgentName.Create("Code");
        await Assert.That(name.Value).IsEqualTo("code");
    }
}
