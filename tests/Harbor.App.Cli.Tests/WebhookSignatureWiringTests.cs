// WebhookSignatureWiringTests.cs — issue #3380.
//
// WebhookSignature was a dead type: its only callers were its own unit tests.
// The CLI now verifies an inbound webhook proof through WebhookSignature.Verify
// in TaskScheduleVerb before it launches the addressed task. These tests drive
// the real dispatch over a temp tasks.json and assert the observable result:
// a valid delivery fires the task, a tampered one is rejected without firing.

using System.Text;
using Harbor.App.Cli.Commands;

namespace Harbor.App.Cli.Tests;

// Swaps the process-wide Console streams to observe whether the task fired,
// so it must not run beside the other console-capturing suites.
[NotInParallel]
public class WebhookSignatureWiringTests
{
    private const string Secret = "topsecret";

    private static string NewStoreWithTask(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "tasks.json");
        var tasks = new List<ScheduledTask>
        {
            new("docs-watch", "*/10 * * * *", "Rebuild the docs", "https://example.com/hook", "2026-11-19T00:00:00.0000000+00:00"),
        };
        File.WriteAllText(path, TaskStore.Serialize(tasks));
        return path;
    }

    private static (int Exit, string Out, string Err) Fire(string path, string body, string signature, string? secret = Secret)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            int exit = TaskScheduleVerb.Run(
                ["fire", "docs-watch", "--payload", body, "--signature", signature],
                path,
                secret);
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Test]
    public async Task ValidWebhookSignature_LaunchesTask()
    {
        string path = NewStoreWithTask(Path.Combine(Path.GetTempPath(), "harbor-webhook-tests-" + Guid.NewGuid().ToString("N")));
        const string body = "{\"event\":\"task.fire\"}";
        string signature = WebhookSignature.Sign(Secret, Encoding.UTF8.GetBytes(body));

        var (exit, output, error) = Fire(path, body, signature);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Fired 'docs-watch': Rebuild the docs");
        await Assert.That(error).IsEmpty();
    }

    [Test]
    public async Task TamperedWebhookSignature_RejectsWithoutLaunching()
    {
        string path = NewStoreWithTask(Path.Combine(Path.GetTempPath(), "harbor-webhook-tests-" + Guid.NewGuid().ToString("N")));
        const string body = "{\"event\":\"task.fire\"}";
        string signature = WebhookSignature.Sign(Secret, Encoding.UTF8.GetBytes(body));

        // Same header, different body: the HMAC proof must fail and the task
        // must never be launched.
        var (exit, output, error) = Fire(path, "{\"event\":\"task.fire\"} ", signature);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains("invalid webhook signature");
        await Assert.That(output).DoesNotContain("Fired");
    }

    [Test]
    public async Task WrongSecretSignature_RejectsWithoutLaunching()
    {
        string path = NewStoreWithTask(Path.Combine(Path.GetTempPath(), "harbor-webhook-tests-" + Guid.NewGuid().ToString("N")));
        const string body = "payload";
        string signature = WebhookSignature.Sign("someone-else", Encoding.UTF8.GetBytes(body));

        var (exit, output, error) = Fire(path, body, signature);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains("invalid webhook signature");
        await Assert.That(output).DoesNotContain("Fired");
    }

    [Test]
    public async Task MissingSecret_FailsClosedWithoutLaunching()
    {
        string path = NewStoreWithTask(Path.Combine(Path.GetTempPath(), "harbor-webhook-tests-" + Guid.NewGuid().ToString("N")));
        const string body = "payload";
        string signature = WebhookSignature.Sign(Secret, Encoding.UTF8.GetBytes(body));

        var (exit, output, error) = Fire(path, body, signature, secret: null);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(error).Contains("webhook secret is not configured");
        await Assert.That(output).DoesNotContain("Fired");
    }
}
