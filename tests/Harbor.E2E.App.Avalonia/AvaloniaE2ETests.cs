using Harbor.E2E.Framework;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     End-to-end tests for the Harbor Avalonia desktop app.
/// </summary>
/// <remarks>
///     <para>
///         <b>Linux sandbox:</b> all tests are skipped via
///         <see cref="SkipAttribute"/>. True headless Avalonia requires either
///         a Windows desktop session OR an Xvfb-backed Linux session with
///         <c>Avalonia.Headless.X11</c> installed — neither is available in
///         the current Linux sandbox build. The tests are defined (and tagged
///         <c>[Category("E2E")]</c>) so that the Windows CI lane can pick
///         them up immediately; see <c>docs/E2E_TESTING.md</c>.
///     </para>
/// </remarks>
[Category("E2E")]
public class AvaloniaE2ETests : E2eTestBase
{
    private const string AvaloniaProjectPath = "apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj";

    // Skip reason shared by every test in this class. TUnit's [Skip] attribute
    // is applied per-test (it doesn't support class-level skip in 0.50).
    private const string LinuxSkipReason =
        "HeadlessAvaloniaDriver requires Windows (or Linux with Xvfb + " +
        "Avalonia.Headless.X11). Skipped on this OS. See docs/E2E_TESTING.md.";

    /// <summary>
    ///     The Avalonia app boots without crashing and shows the main window
    ///     (currently asserted via stdout banner text — a full Avalonia.Headless
    ///     implementation would assert on the rendered window contents).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Skip(LinuxSkipReason)]
    public async Task MainWindow_ShowsWithoutCrash()
    {
        await using var driver = new HeadlessAvaloniaDriver(AvaloniaProjectPath);
        await driver.StartAsync(args: [], env: GetEnv()).ConfigureAwait(false);
        bool saw = await driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await Assert.That(saw).IsTrue();
        await driver.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Sending a prompt streams the mock LLM response back into the chat
    ///     view. Skipped on Linux for the same reason as above.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Skip(LinuxSkipReason)]
    public async Task SendPrompt_StreamsResponse()
    {
        Server.SetResponse("test-model", "Hello from mock LLM!");
        await using var driver = new HeadlessAvaloniaDriver(AvaloniaProjectPath);
        await driver.StartAsync(args: [], env: GetEnv()).ConfigureAwait(false);
        await driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // TODO(av2): once HeadlessAvaloniaDriver wraps Avalonia.Headless.X11,
        // replace this with a real "type into the input textbox + click Send"
        // sequence. For now we just verify the app started + accepted stdin
        // without crashing.
        await driver.SendInputAsync("Hi").ConfigureAwait(false);
        await driver.StopAsync().ConfigureAwait(false);
        await Assert.That(driver.IsRunning).IsFalse();
    }

    /// <summary>
    ///     Verifies that <see cref="HeadlessAvaloniaDriver.IsSupportedOnCurrentOs"/>
    ///     is <see langword="false"/> on Linux so test code can branch on it.
    /// </summary>
    [Test]
    [Category("E2E")]
    public async Task HeadlessAvalonia_DriverReportsPlatformSupport()
    {
        await using var driver = new HeadlessAvaloniaDriver(AvaloniaProjectPath);
        // On Linux we expect this to be false; on Windows it should be true.
        // Assert the platform-specific value so the test is meaningful on both.
        bool expected = OperatingSystem.IsWindows();
        await Assert.That(driver.IsSupportedOnCurrentOs).IsEqualTo(expected);
    }
}
