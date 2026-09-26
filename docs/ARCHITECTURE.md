# How Vexis works

This document explains how Vexis is put together and why it's built that way. It's for anyone
reading the code, reviewing a change or thinking about contributing.

```
                       ┌──────────────────────────── Vexis.exe (C#, .NET 8, runs as admin) ────────────────────────────┐
                       │                                                                                               │
 CPU / board / GPU ──► │  SensorService ──(every 250 ms, background thread)──► SensorData ──► MainForm poll tick         │
 drives (SMART)        │     └─ LibreHardwareMonitor + PawnIO driver                              │                      │
                       │                                                                         ├─► FanController ──────┼──► fan speeds
                       │  StorageReader (SMART + WMI, every 2 s)                                  ├─► alerts / tray       │
                       │  ProcessMonitor, CpuTopology, StartupCheck, DriverSetup                  ├─► CsvRecorder ────────┼──► Documents\Vexis Logs
                       │                                                                         └─► JSON ──► WebView2   │
                       │  UpdateManager ──► GitHub releases        OpenRGB proxy ──► 127.0.0.1:6742                      │
                       └───────────────────────────────────────────────────────────────────────────────┬───────────────┘
                                                                                                       │  receiveData(json)
                                                          WebView2 (https://vexis.local/, local files) ▼  sendToHost({...})
                                                          home / index / gpu / fans / rgb / storage ... .html  +  nav.js
```

## The two halves

**The C# host** (`*.cs`) does everything that touches hardware, Windows or the network. It's a
WinForms app whose window is almost entirely one WebView2 control.

**The interface** (`*.html`, `nav.js`, `ui.css`) is plain HTML/JS, loaded from the install folder
through the virtual address `https://vexis.local/`. No web server, no network.

*Why HTML for the UI?* Themes, text scaling, charts and pop-out windows are much easier in HTML than
in WinForms. Every page shares one navigation/theme script (`nav.js`). Pages can also be opened
in a normal browser with fake data, which is how UI changes are tested and how the README screenshots
are made.

### How the halves talk

- **Host → page:** every 250 ms the host serialises one `SensorData` object to JSON and calls
  `receiveData(json)` in each open window. Settings go the same way through `onConfigReceived`.
- **Page → host:** pages call `sendToHost({type: '...', ...})`. `MainForm.HandleMessage` is a single
  `switch` on `type`.

*Why one message per poll instead of many small calls?* Each page just re-renders from one snapshot,
so pages never get out of sync with each other, and a pop-out window is only a second WebView fed
the same JSON.

`HandleMessage` runs on the UI thread, so anything slow (process list, update download, duplicate
scan, driver repair) is started with `Task.Run` and reports back with `RunScriptEverywhere(...)`.
That keeps the window responsive.

## Sensors (`SensorService.cs`)

- LibreHardwareMonitor (LHM) is polled on its own `System.Threading.Timer`, never the UI thread. If a
  poll is still running when the next is due, the new one is skipped rather than queued.
- **Drives are read every 2 s, not every 250 ms.** Reading SMART data is a command sent to the disk.
  Doing it four times a second would slow other polling and wake drives for nothing.
- **CPU cores are identified by name.** LHM names sensors differently per vendor, generation and
  version (`Core #1`, `CPU Core #1`, `P-Core #1`/`E-Core #1`, loads with or without `Thread #n`).
  `BuildCoreMap` and `AssignThreadLoads` turn all of those into one list of cores, and each naming
  scheme has a unit test.
  - On Intel hybrid CPUs, loads are numbered across the whole CPU while clocks are split into P and
    E, which is why E-cores used to read 0.
  - When LHM lists loads one per logical processor, `CpuTopology` asks Windows
    (`GetLogicalProcessorInformationEx`) which physical core each belongs to.
- **Fallbacks.** If the sensor driver is blocked, CPU clock and load come from Windows performance
  counters and WMI, so the app still shows something useful.

## Fan control (`FanController.cs`, `FanProfiles.cs`)

Fan control is the part that can do real harm, so it's the most defensive:

- **Modes:** each fan is `auto` (BIOS/driver control), `manual` (fixed %), `curve` (its own saved
  curve) or `profile` (a Silent/Balanced/Performance preset). Changing one fan by hand leaves the
  preset and goes back to Custom.
- **Hysteresis.** `CurveSpeed` speeds up straight away but only slows down once the temperature has
  dropped `Hysteresis` °C below the reading that set the current speed. Without it, a CPU hovering
  around a curve point makes fans rev up and down.
- **Minimum speed.** A floor on every curve. Pump presets never go below 60 % because a slowed pump
  heats the CPU quickly.
- **GPU over-temperature guard.** If the GPU core or hot spot passes its limit, GPU fans on manual or
  curve are forced to 100 % until it cools down again.
- **Handing control back.** `RestoreAll()` runs when Vexis exits (normally, from the tray, or through
  a factory reset), so fans never stay stuck at a manual speed.
- **Writes only on change.** Speeds are only sent to the hardware when they change (`LastSent`).

## Other pieces

| File | What it does | Why it's built this way |
|---|---|---|
| `StorageReader.cs` | Drive model, health, temperature, space, lifetime data | SMART via LHM, drive letters via WMI (`Win32_DiskDrive` → partition → logical disk), because LHM doesn't know letters |
| `DuplicateFinder.cs` | Finds identical files | Size → first/last 64 KB → full SHA-256, so almost nothing is fully read unless it's very likely a duplicate. Removal only goes to the Recycle Bin, always keeps one copy, and hashes the file again first in case it changed since the scan |
| `ProcessMonitor.cs` | Processes page | Only sampled while that page is open (about every 1.5 s), CPU % from the change in each process's CPU time between samples. Refuses to end PID ≤ 4, itself, and Windows-critical processes |
| `CsvRecorder.cs` | CSV / text recording | Rows are written as they happen, so a recording survives a crash. Files are opened by name only, never a path |
| `UpdateManager.cs` | Update check and install | Tag must be `v` + `<Version>`. Before running an installer it checks the size, that it's a Windows executable, and the release's SHA-256 file |
| `CrashHelper.cs` | "Vexis didn't close properly" banner | A `running.flag` file that only a clean exit removes. The previous log is kept for the report |
| `StartupManager.cs` | Start with Windows | A Task Scheduler task (runs elevated at sign-in without a UAC prompt), not the registry Run key, which can't start admin apps silently |
| `DriverSetup.cs` / `StartupCheck.cs` | Driver check at launch | Installs/repairs PawnIO from the bundled installer. Only *reads* Windows security settings, except the user-initiated Restore button |
| `Config.cs` | Settings | One JSON file in `%AppData%\Vexis`. Unknown keys are ignored, so older and newer versions can share it |

## Releases

`.github/workflows/release.yml` builds releases on GitHub's servers from a tagged commit:

1. Runs the unit tests (no release if they fail).
2. Builds the app with the commit id embedded.
3. Signs it (when a certificate is configured).
4. Builds the installer.
5. Writes a SHA-256 file.
6. Adds a build-information table to the release notes.

The app shows its commit on **Info → About**, so any copy can be traced back to its exact source.
See [SECURITY.md](../SECURITY.md).

## Known debt / next refactors

`MainForm.cs` (~1,300 lines) does too much: window, tray, message routing, alerts, recording,
updates and more. The plan is to move these, one at a time, into small services that `MainForm`
wires together:

- `AlertService`: limits, latch/cool-down, inbox, tray blink
- `UpdateService`: check, auto-install, progress (wrapping `UpdateManager`)
- `RecordingService`: start/stop/list (wrapping `CsvRecorder`)
- `SettingsService`: config load/save/reset, and the config message to pages
- `MessageRouter`: turns each `sendToHost` type into a call on one of the services, replacing the big `switch`

Each move should be a pull request that changes no behaviour, with tests for the extracted logic
where it can run without hardware.
