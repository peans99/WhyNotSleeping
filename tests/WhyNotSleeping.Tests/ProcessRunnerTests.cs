using System.Diagnostics;

namespace WhyNotSleeping.Tests;

public class ProcessRunnerTests
{
    static ProcessStartInfo PowerShell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(argument);
        return start;
    }

    [Fact]
    public async Task Both_pipes_are_drained_without_deadlock_and_the_exit_code_is_preserved()
    {
        var result = await ProcessRunner.RunAsync(PowerShell("[Console]::Error.Write('e' * 200000); [Console]::Out.Write('o' * 200000); exit 7"), TimeSpan.FromSeconds(15));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(new string('o', 200000), result.Output);
        Assert.Equal(new string('e', 200000), result.Error);
    }

    [Fact]
    public async Task A_stalled_command_times_out_and_is_terminated()
    {
        var file = Path.Combine(Path.GetTempPath(), $"WhyNotSleeping-test-{Guid.NewGuid():N}.pid");
        var timer = Stopwatch.StartNew();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(
                PowerShell($"[IO.File]::WriteAllText('{file.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 30"), TimeSpan.FromSeconds(2)));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8));
            if (File.Exists(file))
            {
                var id = int.Parse(File.ReadAllText(file));
                try
                {
                    using var process = Process.GetProcessById(id);
                    Assert.True(process.WaitForExit(3000), "Timed-out command is still running.");
                }
                catch (ArgumentException) { /* already exited */ }
            }
        }
        finally { File.Delete(file); }
    }
}
