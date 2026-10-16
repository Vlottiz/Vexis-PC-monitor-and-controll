# Vexis update log

<!--
How to use: before a release, add a section at the top:
  ## x.y.z — YYYY-MM-DD
  - What changed, one line each
The version must match <Version> in Pcmonitor2_0.csproj. Vexis shows the section
for the installed version on Info → Version, and the release workflow uses it as
the GitHub release notes.
-->

## 2.13.0 — 2026-10-16
- Security: the interface's browser now runs with its normal security on (the old flags that turned it off are gone), only loads Vexis's own pages, and only accepts commands from them. Developer tools are off in released builds
- Security: names from outside Vexis (process names, recordings, fan, drive and device names) can no longer be read as code by the pages. A program with a crafted name could do that on the Processes page before
- Updates: a release without a SHA-256 checksum is no longer installed automatically, and every release now carries a GitHub-signed record of which commit built it
- Fans: new CPU over-temperature guard. At 95 °C, case, CPU and pump fans under Vexis's control go to full speed until the CPU is 10 °C cooler
- Fans: the health check respects the pump minimum, puts the fan back in the mode it was in (it used to switch it to auto), and no longer fights the normal fan updates while it runs
- Opening the OpenRGB window no longer freezes Vexis for a moment
- Security: the duplicate finder only reveals or recycles files from its own last scan, and only scans real folders; the recordings page only opens Vexis's own recordings
- Fans: if a health check fails partway, the fan is still put back to its setting and the page is told what happened

## 2.12.1 — 2026-10-08
- Fixed: when started with Windows, Vexis could open as a grey window that didn't respond until it was restarted. It now runs only the monitoring, fans and alerts in the tray after sign-in, and builds the interface the first time you open it
- If the interface's browser process crashes (e.g. a graphics driver reset), Vexis reloads or rebuilds it by itself instead of leaving a dead window
- Fans, alerts and recording keep running even while the interface is loading or closed
- Safety: links from the interface only open if they're https:// web pages, so nothing else can be started through them
- Safety: every fan speed is kept between 0 and 100 %, and pumps never go below 50 % (manual, curve or profile)
- Intel: the main CPU temperature now uses the CPU Package sensor instead of the first core it found
- RGB: picking "Direct" on a device (or a saved look that stored "Direct") now sends its colours, instead of leaving the LEDs blank or frozen
- RGB: the ⚙ GUI button opens OpenRGB with its SDK server on, so Vexis reconnects by itself instead of losing control until CLOSE GUI
- Installing or uninstalling now asks Vexis to close by itself first, so fans are handed back properly and there's no "didn't close properly" warning afterwards
- The update log on Info → Version shows 2.12.0 and 2.12.1 again (they were hidden by a mistake in this file)

## 2.12.0 — 2026-09-26
- RGB: release a device (e.g. a keyboard or mouse run by iCUE, Synapse or G HUB). Vexis stops touching it, can put it back on its own lighting mode, and remembers it; fans, RAM and your other RGB keep working. "Take control again" undoes it
- Updates are checked against the release's SHA-256 checksum before they're installed; a download that doesn't match is deleted, not run
- Info → About shows the exact source commit your copy was built from
- Every release now lists its SHA-256, source commit, build date and toolchain, and is only built if the automated tests pass
- Automated tests for fan curves, hysteresis, fan profiles, CPU thread mapping, updates and the duplicate finder
- New SECURITY.md (why admin rights are needed, every network connection, what's stored), CONTRIBUTING.md and docs/ARCHITECTURE.md
- Bug report and feature request forms on GitHub
- LibreHardwareMonitor's license is shown correctly (MPL-2.0)

## 2.11.0 — 2026-09-26
- Saved RGB looks: save your colours, per-LED painting, built-in modes and brightness under a name and put them back with one click; can reapply your last look automatically when Vexis connects to OpenRGB
- Reset to factory settings (System tab and Info page): puts every setting back to how it was at install, turns fans back to automatic and restarts Vexis. CSV recordings are kept
- Intel 12th gen and newer: E-core loads now show up (they were missing, so threads in use read x/16 instead of x/24 on an i9-12900K)
- "Threads in use" always counts all of the CPU's threads (24 on a 12900K), even if a core reports no load

## 2.10.0 — 2026-09-26
- Duplicate file finder on the Storage page: finds identical files, moves chosen copies to the Recycle Bin and always keeps one
- Side menu tabs can be reordered: drag the ⋮⋮ grip; the order is saved (Reset order puts it back)
- Home page shows a live tile for every page, in your side-menu order, plus a Storage card
- This update log on Info → Version

## 2.9.0 — 2026-09-25
- Fan profiles: Silent, Balanced, Performance or Custom — on the Fan Control page and in the tray menu
- Fan curves: hysteresis (no revving up and down) and a minimum speed; saved curves apply again at launch
- Fan curve graph keeps a readable size in wide windows
- New Storage page: drive temperature, health, space, read/write speed, lifetime data and power-on time
- Optional: install updates automatically
- Crash helper: after a crash, open the log or report it on GitHub in one click
- Releases are built automatically on GitHub

## 2.8.2 — 2026-09-25
- Fixed the GPU temperature showing many decimals on the Performance page
- Fan Control shows each fan's real mode (curve / manual / auto)
- Links point to the renamed repository

## 2.8.1 — 2026-09-25
- Fixed threads per core on CPUs where they weren't grouped (e.g. Ryzen 9 9950X3D now shows x/2 per core, x/32 total)
- Thread Viewer activity chart has core labels, a time axis and hover details

## 2.8.0 — 2026-09-25
- New Processes page (like Task Manager): CPU, memory and GPU per app, search, sort, End task
- New Thread Viewer page: every core and thread in detail
- Performance page: busy-thread count per core and an adjustable "thread busy" level

## 2.7.0 — 2026-09-25
- New CSV Recording page: choose what to record, raw CSV or pretty text, and a built-in viewer
- Alerts: the tray icon blinks and Vexis lists what triggered until you clear it
- Performance page: dots show which threads of each core are busy

## 2.6.0 — 2026-09-24
- GPU fan control (NVIDIA and AMD) with an over-temperature guard
- Alerts for GPU hot spot and GPU memory temperature
- One-click updates from the Info page
- Driver check and loading screen at every launch

## 2.5.0 — 2026-09-23
- New GPU page with full GPU metrics, history charts and every sensor

## 2.4.7 — 2026-09-23
- Brightness per RGB device; software RGB effects paused while they are improved

## 2.4.6 — 2026-09-23
- RGB brightness slider; all pages share the same size; fixed overlapping buttons
