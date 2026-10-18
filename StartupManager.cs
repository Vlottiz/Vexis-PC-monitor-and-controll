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
        var (exit, _) = SysTools.Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return exit == 0;
    }

    /// <summary>Returns the resulting state (true = enabled).</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                var (exit, output) = SysTools.Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
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

            // The task runs Vexis elevated at every sign-in, so the XML must be exactly what was
            // written here. It goes in a new random folder (nothing can be planted beforehand),
            // is read back through a read-only handle that stays open while schtasks imports it
            // (nothing can change it in between), and is only imported if it reads back the same.
            string dir  = Path.Combine(Path.GetTempPath(), "Vexis-Task-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "task.xml");
            int code; string text;
            try
            {
                using (var w = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(w, Encoding.Unicode)) sw.Write(xml);   // matches encoding="UTF-16"
                using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (var sr = new StreamReader(held, Encoding.Unicode, false, 4096, leaveOpen: true))
                    if (sr.ReadToEnd() != xml) throw new IOException("the task file changed before it was imported");
                (code, text) = SysTools.Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }   // a locked leftover goes with %TEMP% cleanup
            Console.WriteLine($"[startup] Start with Windows on (schtasks exit {code}) {text.Trim()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] Start with Windows failed: {ex.Message}");
        }
        return IsEnabled();
    }

}
