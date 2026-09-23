using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

/// <summary>
/// "Start with Windows" via a Task Scheduler logon task.
///
/// Vexis needs admin rights, so a Run registry key would either raise a UAC prompt
/// at every sign-in or be skipped. A scheduled task with "run with highest
/// privileges" starts it elevated without a prompt. The task is created from XML
/// because schtasks' command-line defaults would stop it on battery and kill it
/// after 3 days (ExecutionTimeLimit).
/// </summary>
public static class StartupManager
{
    private const string TaskName = "Vexis";

    public static bool IsEnabled()
    {
        var (exit, _) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return exit == 0;
    }

    /// <summary>Returns the resulting state (true = enabled).</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                var (exit, output) = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
                Console.WriteLine($"[startup] Start with Windows off (schtasks exit {exit}) {output.Trim()}");
                return IsEnabled();
            }

            string exe  = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            string user = WindowsIdentity.GetCurrent().Name;
            string xml  = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Description>Starts Vexis Hardware Monitoring minimized to the tray when you sign in.</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{SecurityElement.Escape(user)}</UserId>
                      <Delay>PT10S</Delay>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{SecurityElement.Escape(user)}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{SecurityElement.Escape(exe)}</Command>
                      <Arguments>--minimized</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;

            string file = Path.Combine(Path.GetTempPath(), "vexis-startup-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode); // matches encoding="UTF-16"
            var (code, text) = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            try { File.Delete(file); } catch { }
            Console.WriteLine($"[startup] Start with Windows on (schtasks exit {code}) {text.Trim()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] Start with Windows failed: {ex.Message}");
        }
        return IsEnabled();
    }

    private static (int exit, string output) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (p == null) return (-1, "");
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
