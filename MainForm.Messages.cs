using System.Text.Json;

// Page → app messages, one handler per area. MainForm.HandleMessage parses the JSON and
// asks each handler in turn; a handler returns false for a type it doesn't know.
// Every message type a page sends must be handled here: MessageContractTests checks that.
public partial class MainForm
{
    // ── Window, navigation, logs and the crash banner ───────────────────────────────
    private bool HandleWindowMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            case "getLogs":
                int afterSeq = msg.seq ?? 0;
                var (logLines, newSeq) = AppLog.GetSince(afterSeq);
                if (logLines.Length > 0)
                {
                    var logJson = System.Text.Json.JsonSerializer.Serialize(
                        new { lines = logLines, seq = newSeq });
                    Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                        $"typeof updateLogs==='function'&&updateLogs({logJson})"); });
                }
                break;

            case "zoom":
                Invoke(() => ZoomHelper.Change(_webView, msg.delta ?? 0)); // ZoomFactorChanged saves + updates the label
                break;

            case "factoryReset":
                Invoke(FactoryReset);
                break;

            case "requestConfig":
                _ = SendConfigViaScript();
                // Also push current sensor data immediately so the page
                // doesn't have to wait up to 250ms for the next poll tick.
                // This is what makes fans.html show data right away on navigate.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100); // brief delay for config to settle
                    try
                    {
                        var snap = _sensor.GetLatestData();
                        snap.fans = _fans.GetSnapshot();
                        string snapJson = JsonSerializer.Serialize(snap, _json);
                        Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                            $"typeof receiveData==='function'&&receiveData({snapJson})"); });
                    }
                    catch (Exception ex) { Console.WriteLine($"[popout] First data push failed: {ex.Message}"); }
                });
                break;

            // Page change from the side menu. Navigation is locked to vexis.local
            // (WebViewSecurity), so a path here can only open one of the app's own files
            case "navigate":
                if (msg.file != null)
                    _webView.CoreWebView2.Navigate($"https://vexis.local/{msg.file}");
                break;

            case "popOut":
                if (PopoutForm.IsKnownPage(msg.page)) Invoke(() => OpenPopout(msg.page!));
                else Console.WriteLine($"[msg] popOut refused (unknown page): {msg.page}");
                break;

            case "openUrl":
                // Shell-execute opens whatever it's given — a web page, but also a program
                // or a file. Only hand it https:// links so a page can never start anything else.
                if (SafeLinks.IsWebLink(msg.url, out var link))
                    SysTools.OpenUrl(link);   // via Explorer, so the browser doesn't get admin rights
                else
                    Console.WriteLine($"[msg] openUrl refused (not an https link): {msg.url}");
                break;

            case "crashOpenLog":  CrashHelper.OpenLog(); break;
            case "crashReport":   CrashHelper.OpenReport(_lastData?.info); break;
            case "crashDismiss":  CrashHelper.Dismiss(); break;

            case "minimize": Invoke(() => { WindowState = FormWindowState.Minimized; }); break;
            case "close":    Invoke(() => Hide()); break;   // to the tray; Vexis keeps running
            case "toggleFullscreen": Invoke(() => ToggleFullscreen()); break;
            case "exit":
                Invoke(() => { _tray.Visible = false; Application.Exit(); });
                break;
            default: return false;
        }
        return true;
    }

    // ── Settings, colours and Start with Windows ────────────────────────────────────
    private bool HandleSettingsMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            case "setStartup":
            {
                bool want = msg.enabled == true;
                _ = Task.Run(() =>
                {
                    StartWithWindowsEnabled = StartupManager.SetEnabled(want);
                    RunScriptEverywhere(
                        $"typeof navSetStartupState==='function'&&navSetStartupState({(StartWithWindowsEnabled ? "true" : "false")})");
                });
                break;
            }

            // ── Settings from the pages, merged into config.json ──
            case "saveSettings":
                if (msg.settings != null)
                {
                    _config.Settings ??= new Dictionary<string,string>();
                    foreach (var kv in msg.settings)
                        _config.Settings[kv.Key] = kv.Value;
                    _config.Save();
                    Console.WriteLine("[config] Settings saved");
                    if (msg.settings.Keys.Any(k => k.StartsWith("alert")))
                    {
                        // New limits/state: evaluate from scratch (no latch, no cooldown)
                        _alertActive.Clear(); _alertLast.Clear();
                        var st = _config.Settings;
                        Console.WriteLine($"[alert] settings: enabled={st.GetValueOrDefault("alertsEnabled", "false")} " +
                                          $"cpu={st.GetValueOrDefault("alertCpu", "90")} gpu={st.GetValueOrDefault("alertGpu", "85")}");
                    }
                }
                break;

            case "saveColors":
                if (msg.colors != null)
                    foreach (var kv in msg.colors)
                        _config.Colors[kv.Key] = kv.Value;
                // The page sends which preset it was; it's sent back at start-up (RESET ALL uses it)
                if (msg.presetIdx is int pi && pi >= 0) _config.LastPresetIdx = pi;
                _config.Save();
                break;

            case "saveProfile":
                if (msg.slot.HasValue)
                { _config.ColorProfiles[msg.slot.Value] = msg.colors ?? new(); _config.Save(); }
                break;

            case "deleteProfile":
                if (msg.slot.HasValue)
                { _config.ColorProfiles.Remove(msg.slot.Value); _config.Save(); }
                break;
            default: return false;
        }
        return true;
    }

    // ── CSV recordings and the duplicate finder ─────────────────────────────────────
    private bool HandleFileMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            case "recordStart":
                _recorder.Start(_lastData ?? _sensor.GetLatestData(), msg.record);
                PushRecordings();
                break;

            case "recordStop":
                _recorder.Stop();
                PushRecordings();
                break;

            case "listRecordings":
                PushRecordings();
                break;

            case "readRecording":
            {
                var path = CsvRecorder.ResolveFile(msg.file);
                if (path == null) break;
                // Shared read: the file may still be being recorded
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int Max = 25 * 1024 * 1024;
                var buf = new byte[Math.Min(fs.Length, Max)];
                int read = 0, n;
                while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                string text = System.Text.Encoding.UTF8.GetString(buf, 0, read).TrimStart('\uFEFF');
                string json = JsonSerializer.Serialize(new { name = Path.GetFileName(path), text, truncated = fs.Length > Max }, _json);
                RunScriptEverywhere($"typeof onRecordingData==='function'&&onRecordingData({json})");
                break;
            }

            case "showRecording":
            {
                var path = CsvRecorder.ResolveFile(msg.file);
                if (path != null) SysTools.RevealFile(path);
                break;
            }

            case "deleteRecording":
            {
                var path = CsvRecorder.ResolveFile(msg.file);
                if (path != null && !(_recorder.IsRecording && _recorder.Status.file == msg.file))
                {
                    try { File.Delete(path); Console.WriteLine($"[record] Deleted {msg.file}"); }
                    catch (Exception ex) { Console.WriteLine($"[record] Delete failed: {ex.Message}"); }
                }
                PushRecordings();
                break;
            }

            case "openLogFolder":
                Directory.CreateDirectory(CsvRecorder.Folder);
                System.Diagnostics.Process.Start(SysTools.Explorer, $"\"{CsvRecorder.Folder}\"");
                break;

            // ── Storage page: duplicate file finder ─────────────────────────
            case "dupPickFolder":
            {
                using var dlg = new FolderBrowserDialog { Description = "Choose a folder to search for duplicate files", UseDescriptionForTitle = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    RunScriptEverywhere($"typeof onDupFolder==='function'&&onDupFolder({JsonSerializer.Serialize(dlg.SelectedPath)})");
                break;
            }
            case "dupStart":
            {
                // Only full paths to folders that exist; anything else from the page is dropped
                var folders = (msg.dup?.paths ?? new()).Select(DuplicateFinder.CheckFolder).OfType<string>().ToList();
                if (folders.Count > 0 && !_dupes.Running)
                    _dupes.Start(new DuplicateFinder.Options(folders, (long)(Math.Clamp(msg.dup!.minMb, 0, 1_000_000) * 1048576), msg.dup.includeSystem),
                        pr => RunScriptEverywhere($"typeof onDupProgress==='function'&&onDupProgress({JsonSerializer.Serialize(pr, _json)})"),
                        res => RunScriptEverywhere($"typeof onDupResults==='function'&&onDupResults({JsonSerializer.Serialize(res, _json)})"));
                break;
            }
            case "dupCancel":
                _dupes.Cancel();
                break;
            case "dupRecycle":
                if (msg.dup?.files is { Count: > 0 } files)
                    _ = Task.Run(() =>
                    {
                        var (removed, freed, skipped) = _dupes.Recycle(files);
                        RunScriptEverywhere($"typeof onDupRecycled==='function'&&onDupRecycled({JsonSerializer.Serialize(new { removed, freed, skipped }, _json)})");
                    });
                break;
            case "dupReveal":
                // Only files the last scan found, never an arbitrary path from the page
                if (_dupes.IsInResults(msg.file) && File.Exists(msg.file))
                    SysTools.RevealFile(msg.file);
                break;
            default: return false;
        }
        return true;
    }

    // ── Processes page ──────────────────────────────────────────────────────────────
    private bool HandleProcessMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            // ── Processes page (Task-Manager-style list) ──────────────────────
            case "getProcesses":                 // sampled off the UI thread (~100 ms)
            {
                bool reset = msg.enabled == true;       // page just opened: resend icons
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (reset) _procs.ResetIcons();
                        var snap = _procs.Sample();
                        RunScriptEverywhere($"typeof onProcesses==='function'&&onProcesses({JsonSerializer.Serialize(snap, _json)})");
                    }
                    catch (Exception ex) { Console.WriteLine($"[procs] {ex.Message}"); }
                });
                break;
            }

            case "endTask":
            {
                int? pid = msg.pid; string? exe = msg.file; long? started = msg.started;
                _ = Task.Run(() =>
                {
                    var (ok, text) = _procs.End(pid, exe, started);
                    RunScriptEverywhere($"typeof onEndTask==='function'&&onEndTask({(ok ? "true" : "false")},{JsonSerializer.Serialize(text)})");
                });
                break;
            }
            default: return false;
        }
        return true;
    }

    // ── Security page, alerts and updates ───────────────────────────────────────────
    private bool HandleSecurityMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            case "clearAlerts":
                Invoke(ClearInbox);
                break;

            case "testAlert":
                // 5 s delay so there is time to minimize Vexis and see the tray icon blink
                _ = Task.Delay(5000).ContinueWith(_ => BeginInvoke(() => QueueNotification("test alert",
                    "Temperature alerts are working. You'll see this when a limit is passed.",
                    ToolTipIcon.Info)));
                break;

            case "getSecurityStatus":
                PushSecurityStatus();
                break;

            case "runStartupCheck":
                _ = Task.Run(() => { StartupCheck.Run(); PushSecurityStatus(); });
                break;

            case "installPawnIO":
                _ = Task.Run(() => { DriverSetup.EnsurePawnIo(forceRepair: true); PushSecurityStatus(); });
                break;

            case "restoreSecurity":
                _ = Task.Run(() => { DriverSetup.RestoreWindowsSecurity(); PushSecurityStatus(); });
                break;

            case "checkUpdate":                 // enabled:true = "check again" button
                PushUpdateInfo(force: msg.enabled == true);
                break;

            case "installUpdate":
                _ = Task.Run(InstallUpdateAsync);
                break;
            default: return false;
        }
        return true;
    }

    // ── OpenRGB ─────────────────────────────────────────────────────────────────────
    private bool HandleRgbMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            // ── RGB ──
            // The RGB page can't open a TCP socket itself, so it sends OpenRGB requests here;
            // the reply comes back through orgbProxyResult(callId, ...) matched by callId
            case "openrgbProxy":
                Console.WriteLine($"[rgb] proxy: {msg.method} {msg.path}");
                _ = Task.Run(async () =>
                {
                    string cid2 = msg.callId ?? "0";
                    var (ok, json) = await OpenRgbProxyAsync(msg);
                    string payload = System.Text.Json.JsonSerializer.Serialize(json);
                    Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                        $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid2}',{(ok ? "true" : "false")},{payload})"); });
                });
                break;

            case "killOpenRGB":
                OpenRgbLauncher.StopAll();
                _orgbClient?.ResetFailedDevices();
                _orgbClient = null;
                break;

            case "checkOpenRGB":
            {
                string cid3 = msg.callId ?? "0";
                string runEsc = JsonSerializer.Serialize(OpenRgbLauncher.IsRunning() ? "{\"running\":true}" : "{\"running\":false}");
                Invoke(() => { _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                    $"typeof orgbProxyResult==='function'&&orgbProxyResult('{cid3}',true,{runEsc})"); });
                break;
            }

            case "launchOpenRGB":
                if (OpenRgbLauncher.IsRunning()) { Console.WriteLine("[rgb] OpenRGB already running"); break; }
                _sensor.PausePoll(5000);   // OpenRGB scans the RAM bus at start; keep LHM off it meanwhile
                OpenRgbLauncher.Start();
                break;

            case "launchOpenRGBGui":
                // Restart it so its window opens; wait off the UI thread while the old copy exits
                _orgbClient = null;
                _ = Task.Run(async () =>
                {
                    OpenRgbLauncher.StopAll();
                    await Task.Delay(800);
                    OpenRgbLauncher.Start();
                });
                break;
            default: return false;
        }
        return true;
    }

    // ── Fan control ─────────────────────────────────────────────────────────────────
    private bool HandleFanMessage(IncomingMessage msg)
    {
        switch (msg.type)
        {
            // Changing one fan by hand leaves a preset profile (back to Custom)
            case "fanSetManual":
                if (msg.fanName != null) { _fans.LeavePreset(_config); _fans.SetManual(msg.fanName, msg.value ?? 50f); UpdateTrayProfile(); }
                break;
            case "setFanPump":
                if (msg.fanName != null) _fans.SetPump(msg.fanName, msg.enabled == true, _config);
                break;
            case "fanSetAuto":
                if (msg.fanName != null) { _fans.LeavePreset(_config); _fans.SetAuto(msg.fanName); UpdateTrayProfile(); }
                break;
            case "fanSetCurveMode":
                if (msg.fanName != null) { _fans.LeavePreset(_config); _fans.SetCurveMode(msg.fanName, msg.enabled ?? false); UpdateTrayProfile(); }
                break;
            case "saveFanCurve":
                if (msg.fanCurve != null)
                {
                    _fans.LeavePreset(_config);
                    _config.UpsertCurve(msg.fanCurve); _fans.SetCurveMode(msg.fanCurve.FanName, msg.fanCurve.Enabled);
                    UpdateTrayProfile();
                }
                break;
            case "setFanProfile":
                SetFanProfile(msg.profile);
                break;

            // Takes ~8 s, so it runs on a worker thread; the result goes back as a web message
            case "fanHealthCheck":
                if (msg.fanName != null)
                {
                    string name = msg.fanName;
                    Task.Run(() =>
                    {
                        var result = _fans.RunHealthCheck(name);
                        string rjson = JsonSerializer.Serialize(new
                        {
                            type="healthResult", fanName=result.FanName,
                            status=result.Status, message=result.Message, readings=result.Readings
                        }, _json);
                        Invoke(() => _webView.CoreWebView2?.PostWebMessageAsString(rjson));
                    });
                }
                break;
            default: return false;
        }
        return true;
    }

}
