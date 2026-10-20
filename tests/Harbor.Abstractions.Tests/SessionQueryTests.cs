using Harbor.Abstractions.Sessions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     The session-query CONTRACT (#1743): the filter algebra, the window and
///     the bounds a query may not cross. Every rejected query is a failure on
///     the Result rail, never an exception.
/// </summary>
public class SessionQueryTests
{
    [Test]
    public async Task Validate_DefaultOptions_Succeeds()
    {
        var result = new SessionQueryOptions().Validate();

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Validate_LimitBelowMinimum_Fails()
    {
        var result = new SessionQueryOptions { Limit = 0 }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("limit");
    }

    [Test]
    public async Task Validate_LimitAboveMaximum_Fails()
    {
        var result = new SessionQueryOptions { Limit = SessionQueryOptions.MaxLimit + 1 }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("limit");
    }

    [Test]
    public async Task Validate_NegativeOffset_Fails()
    {
        var result = new SessionQueryOptions { Offset = -1 }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("offset");
    }

    [Test]
    public async Task Validate_InvertedWindow_Fails()
    {
        var window = new SessionQueryWindow(
            new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));

        var result = new SessionQueryOptions { Window = window }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("window");
    }

    [Test]
    public async Task Validate_BlankText_Fails()
    {
        var result = new SessionQueryOptions { Text = "   " }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("text");
    }

    [Test]
    public async Task Validate_UnknownRecordKind_Fails()
    {
        var result = new SessionQueryOptions
        {
            RecordKinds = [(SessionRecordKind)99]
        }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("kind");
    }

    [Test]
    public async Task Validate_BlankSessionId_Fails()
    {
        var result = new SessionQueryOptions { SessionIds = ["ok", " "] }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Session id");
    }

    [Test]
    public async Task Validate_BlankRole_Fails()
    {
        var result = new SessionQueryOptions { Roles = [""] }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Role");
    }

    [Test]
    public async Task Validate_BlankEventType_Fails()
    {
        var result = new SessionQueryOptions { EventTypes = [" "] }.Validate();

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Event type");
    }

    [Test]
    public async Task Validate_EmptyValueList_IsAFilterNotAnError()
    {
        // The filter algebra: an EMPTY value list means "nothing matches", so
        // it is a legal query — one that answers with no hits.
        var result = new SessionQueryOptions { SessionIds = [] }.Validate();

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Window_Unbounded_ContainsEveryInstant()
    {
        var window = SessionQueryWindow.Unbounded;

        await Assert.That(window.IsEmpty).IsFalse();
        await Assert.That(window.Contains(DateTimeOffset.MinValue)).IsTrue();
        await Assert.That(window.Contains(DateTimeOffset.MaxValue)).IsTrue();
    }

    [Test]
    public async Task Window_BoundsAreInclusive()
    {
        var from = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var window = new SessionQueryWindow(from, to);

        await Assert.That(window.Contains(from)).IsTrue();
        await Assert.That(window.Contains(to)).IsTrue();
        await Assert.That(window.Contains(from.AddSeconds(-1))).IsFalse();
        await Assert.That(window.Contains(to.AddSeconds(1))).IsFalse();
    }

    [Test]
    public async Task Window_StartAfterEnd_IsEmpty()
    {
        var from = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

        await Assert.That(new SessionQueryWindow(from, to).IsEmpty).IsTrue();
    }

    [Test]
    public async Task Window_HalfOpen_SelectsOneSide()
    {
        var from = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        await Assert.That(new SessionQueryWindow(from, null).Contains(to.AddDays(1))).IsTrue();
        await Assert.That(new SessionQueryWindow(from, null).Contains(from.AddDays(-1))).IsFalse();
        await Assert.That(new SessionQueryWindow(null, to).Contains(from.AddDays(-1))).IsTrue();
        await Assert.That(new SessionQueryWindow(null, to).Contains(to.AddDays(1))).IsFalse();
    }
}
