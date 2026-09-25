# Vexis update log

<!--
How to use: before a release, add a section at the top:
  ## 2.11.0 — 2026-10-01
  - What changed, one line each
The version must match <Version> in Pcmonitor2_0.csproj. Vexis shows the section
for the installed version on Info → Version, and the release workflow uses it as
the GitHub release notes.
-->

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
