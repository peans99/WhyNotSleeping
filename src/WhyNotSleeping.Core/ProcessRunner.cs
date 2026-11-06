using System.ComponentModel;
using System.Diagnostics;

namespace WhyNotSleeping;

/// <summary>Drains both output pipes together and bounds the entire command, including reading its output.</summary>
internal static class ProcessRunner
{
    internal sealed record Result(int ExitCode, string Output, string Error);

    public static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {start.FileName}.");
        using var cancellation = new CancellationTokenSource();
        var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var error = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellation.Token), output, error).WaitAsync(timeout).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* it may have exited meanwhile */ }
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} didn't finish within {Format.Duration(timeout)}. Try refreshing again.");
        }
    }
}
