using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace WhyNotSleeping.Gui;

/// <summary>
/// Starting with Windows, in the tray. A Run registry key would bring up the UAC prompt at every sign-in, since the
/// app asks for administrator rights; a scheduled task that runs with highest privileges at logon starts it elevated
/// without asking. Creating or removing the task needs administrator rights.
/// </summary>
internal static class Startup
{
    const string TaskName = "WhyNotSleeping";

    /// <summary>Whether the task exists; stale when it starts an exe somewhere else (the app was moved).</summary>
    public static bool IsEnabled(out bool stale)
    {
        stale = false;
        var (exit, output) = Schtasks("/Query", "/TN", TaskName, "/XML");
        if (exit != 0) return false;
        stale = !output.Contains(SecurityElement.Escape(Command().Exe), StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Creates or updates the task. Returns why it failed, or null.</summary>
    public static string? Enable()
    {
        var file = Path.Combine(Path.GetTempPath(), $"WhyNotSleeping-task-{Environment.ProcessId}.xml");
        try
        {
            // schtasks reads the XML in the encoding it declares.
            File.WriteAllText(file, TaskXml(), Encoding.Unicode);
            var (exit, output) = Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
            return exit == 0 ? null : Failure("create", exit, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't write the startup task: {ex.Message}";
        }
        finally
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes the task. Returns why it failed, or null.</summary>
    public static string? Disable()
    {
        var (exit, output) = Schtasks("/Delete", "/TN", TaskName, "/F");
        return exit == 0 ? null : Failure("remove", exit, output);
    }

    static string Failure(string what, int exit, string output) =>
        $"Windows couldn't {what} the startup task (schtasks exit code {exit}).{(output.Trim() is { Length: > 0 } text ? "\n\n" + text : "")}";

    /// <summary>This exe, or under `dotnet run` dotnet with this app's dll.</summary>
    static (string Exe, string Arguments) Command()
    {
        var exe = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (exe, $"\"{Environment.GetCommandLineArgs()[0]}\" --tray")
            : (exe, "--tray");
    }

    internal static string TaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var (exe, arguments) = Command();
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts WhyNotSleeping in the notification area when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Runs schtasks.exe. Only the exit code is read for decisions: its messages are in the Windows display language.</summary>
    static (int Exit, string Output) Schtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            var result = ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return (result.ExitCode, result.Output + result.Error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
