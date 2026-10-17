using System.Diagnostics;
namespace Harbor.Tools.Builtin;

/// <summary>
///     Production <see cref="IProcessProxy" /> (#1590): launches a real
///     `System.Diagnostics.Process` behind the seam. The PipeDrain guard
///     (tests/Harbor.Architecture.Tests/ChildProcessPipeDrainRules.cs) grades
///     this member, so both redirected streams are wired to their line events
///     in the same member that builds the psi — a drain the callee owns.
/// </summary>
public sealed class SystemProcessProxy : IProcessProxy
{
    /// <inheritdoc cref="IProcessProxy.Start" />
    public Result<IProcessHandle> Start(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory
        };

        foreach (string argument in spec.Arguments)
            psi.ArgumentList.Add(argument);

        if (spec.Environment is not null)
        {
            foreach ((string k, string v) in spec.Environment)
                psi.Environment[k] = v;
        }

        var process = new Process { StartInfo = psi };
        var handle = new SystemProcessHandle(process);
        // Subscribed BEFORE Start: the BCL raises OutputDataReceived only after
        // BeginOutputReadLine, and BashTool attaches to the handle's events
        // before it calls BeginReading (the buffering boundary) — so no line
        // can be missed even though this wiring already exists.
        process.OutputDataReceived += (_, e) => handle.StdoutLine?.Invoke(e.Data);
        process.ErrorDataReceived += (_, e) => handle.StderrLine?.Invoke(e.Data);

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return Result.Failure<IProcessHandle>("Failed to start process.");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            return Result.Failure<IProcessHandle>($"Failed to start process: {ex.Message}");
        }

        return Result.Success<IProcessHandle>(handle);
    }
}
