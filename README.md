<div align="center">

# VEXIS

**Hardware monitoring, fan control and RGB — in one Windows app.**

[![Latest release](https://img.shields.io/github/v/release/Vlottiz/Vexis-PC-monitor-and-controll?style=flat-square&color=ffcc00&label=release)](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Vlottiz/Vexis-PC-monitor-and-controll/total?style=flat-square&color=00ffcc)](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/releases)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-lightgrey?style=flat-square&logo=windows)](#requirements)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE.txt)
[![Buy me a coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-support-ffd000?style=flat-square&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/reyobu)

### [⬇ Download the latest version](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/releases/latest)

<img src="docs/screenshots/home.png" alt="Vexis home dashboard" width="900">

</div>

---

## Why Vexis?

Most PCs end up with three or four vendor apps just to see temperatures, set fan curves and change the RGB. Vexis puts it all in one lightweight app:

- **Everything live** — per-core clocks and threads, temperatures, GPU, memory, drives, fans and processes
- **Real control** — fan profiles and curves for motherboard *and* GPU fans, RGB for 500+ devices through OpenRGB
- **Handy tools** — CSV recording, a duplicate file finder, temperature alerts and a driver check on every launch
- **Works with Windows security on** — uses the signed PawnIO driver; Memory Integrity and VBS stay enabled
- **Yours to style** — 8 themes, full colour editing, text scaling, your own menu order, pop-out windows for a second monitor
- **Keeps itself current** — one-click (or automatic) updates, with a log of what changed

---

## Screenshots

> Screenshots show the real interface with sample readings.

| Performance | Thread Viewer |
|---|---|
| ![Performance](docs/screenshots/performance.png) | ![Thread Viewer](docs/screenshots/thread-viewer.png) |
| **Per-core clocks with threads in use, CCD groups, thermals** | **Every hardware thread: live map, activity timeline, core table** |

| GPU | Processes |
|---|---|
| ![GPU](docs/screenshots/gpu.png) | ![Processes](docs/screenshots/processes.png) |
| **Load, clocks, hot spot and VRAM temps, power, FPS, history** | **Task-Manager-style list with CPU, memory and GPU per app** |

| Storage | Duplicate Finder |
|---|---|
| ![Storage](docs/screenshots/storage.png) | ![Duplicate finder](docs/screenshots/duplicate-finder.png) |
| **Temperature, health, space and speed for every drive** | **Find identical files and send the extras to the Recycle Bin** |

| Fan Control | RGB Control |
|---|---|
| ![Fan control](docs/screenshots/fans.png) | ![RGB](docs/screenshots/rgb.png) |
| **Fan profiles, plus auto / manual / curve for every fan** | **OpenRGB devices, brightness, hardware modes** |

| Temperatures | Memory |
|---|---|
| ![Temperatures](docs/screenshots/temps.png) | ![Memory](docs/screenshots/memory.png) |
| **Every sensor with current, min, max and average** | **Usage, DIMM temperatures, history** |

| CSV Recording | Info & Updates |
|---|---|
| ![CSV recording](docs/screenshots/csv-recording.png) | ![Update log](docs/screenshots/update-log.png) |
| **Record what you choose; built-in viewer with charts** | **Update check and what changed in your version** |

| Settings | |
|---|---|
| ![Settings](docs/screenshots/settings.png) | **Text scaling, start with Windows, automatic updates, temperature alerts** |

---

## Features

### Monitoring
| | |
|---|---|
| **CPU** | Per-core clocks, threads in use per core (adjustable busy threshold), temperatures, CCD temps and V-Cache / Compute groups on Ryzen, P- and E-cores on Intel, package power |
| **Thread Viewer** | Every core and hardware thread: live thread map, labelled activity timeline, sortable core table with session peaks and averages |
| **GPU** | Load (core, memory controller, video engine, bus, power limit), core / hot-spot / memory temperatures, clocks, power, VRAM, fans, PCIe traffic, FPS, history charts and every raw sensor |
| **Memory** | RAM and virtual memory usage, DDR5 DIMM temperatures, load history |
| **Storage** | Every drive: temperature, health (SSD life left), space used, read / write speed, lifetime data written, power-on time, drive letters and type |
| **Duplicate Finder** | On the Storage page: finds files with identical contents on the drives or folders you pick, sorted by space wasted. Keep-oldest / keep-newest selection, and removal goes to the Recycle Bin — at least one copy is always kept |
| **Temperatures** | Every thermal sensor grouped by component, with min / max / average |
| **Processes** | Grouped by app with icons; CPU, memory and GPU per process; search, sort, pause, End task (Windows-critical processes are protected) |

### Control
| | |
|---|---|
| **Fan Profiles** | **Silent**, **Balanced** and **Performance** presets for every fan at once, or **Custom** — switch on the Fan Control page or straight from the tray icon |
| **Fan Control** | Auto / Manual / Curve for motherboard and GPU fans (NVIDIA & AMD). Curves follow CPU, CCD, GPU core, GPU hot spot or VRAM temperature, with **hysteresis** (no revving up and down) and a **minimum speed**. GPU fans are forced to 100% if the GPU overheats, and every fan goes back to BIOS / driver control when Vexis closes. Health check per fan. |
| **RGB Control** | OpenRGB integration: quick colours for all devices, master and per-device brightness, hardware modes (rainbow, breathing…), per-LED painting, RAM stick sync, **saved looks** you can bring back with one click, and **release** any device (e.g. a keyboard run by its own software) so Vexis leaves it alone |

### Tools
| | |
|---|---|
| **Temperature Alerts** | Tray notification when CPU, GPU, GPU hot spot or VRAM passes your limit. The tray icon blinks and the app lists what triggered until you clear it. |
| **CSV Recording** | Choose what to record, raw CSV (Excel / Sheets) or pretty aligned text with a summary, and the row interval. Built-in viewer with summary, chart and table. Rows are written as they happen, so a log survives a crash. |
| **Driver Check** | Every launch: repairs the PawnIO sensor driver if needed and reports a missing graphics driver or devices with driver problems |
| **Updates** | New release on GitHub → **Update now** downloads, installs and restarts Vexis — or switch on **Install updates automatically** |
| **Crash helper** | If Vexis didn't close properly, the next launch keeps the log and offers **Open log** or **Report on GitHub** with the details filled in |

### Comfort
| | |
|---|---|
| **Themes** | 8 presets plus full per-colour editing and 3 saved profiles |
| **Scaling** | Separate sliders for page text, data values and the navigation panel, plus zoom buttons |
| **Your layout** | Drag the ⋮⋮ grip in the side menu to put the pages in any order — saved, and the Home page tiles follow it |
| **Update log** | Info → Version shows what changed in your version (from `CHANGELOG.md`) |
| **Pop-out windows** | Open any page in its own window — great for a second monitor |
| **Start with Windows** | Starts minimized to the tray at sign-in, no UAC prompt |
| **Remembers you** | Window size and position, collapsed sections, settings |
| **Factory reset** | System tab or Info page: puts every setting back to how it was at install and restarts (your recordings are kept) |

---

## Installation

1. Download **`VexisHM-Setup.exe`** from the [latest release](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/releases/latest).
2. Run it and click through the installer.
3. Start Vexis from the Desktop or Start Menu. It asks for administrator rights — these are needed to read hardware sensors.

The installer:
- installs Vexis to `C:\Program Files\Vexis` with Desktop and Start Menu shortcuts,
- installs the **PawnIO** sensor driver if it is missing,
- installs the **Microsoft WebView2 Runtime** on Windows 10 if it is missing (needs internet once),
- adds Vexis to *Apps & features* for a clean uninstall.

> **"Windows protected your PC"?** Vexis isn't code-signed yet, so SmartScreen warns about new downloads. Click **More info → Run anyway**.
> To make sure your download is the real thing, compare `Get-FileHash .\VexisHM-Setup.exe` (PowerShell) with the **SHA-256** in the release notes. Every release lists it together with the exact source commit it was built from.

### Updating

Vexis checks GitHub for a new release when it starts. When one is out, an **UPDATE** badge appears at the top right — click it, then **Update now**. Your settings are kept, and the download is checked against the release's SHA-256 checksum before it's installed.
Prefer hands-off? **Settings → System → Install updates automatically** installs new versions at launch.

---

## Requirements

- **Windows 10 or 11, 64-bit** — Intel or AMD processor
- **Administrator rights** (Vexis asks when it starts)
- Nothing else — the .NET runtime is bundled, WebView2 and PawnIO come with the installer
- **For RGB:** [OpenRGB](https://openrgb.org) installed

### Compatibility

| | Supported | Notes |
|---|---|---|
| **Intel CPUs** | Temperatures, per-core clocks, load, threads, power | 12th gen+ P-cores and E-cores shown separately |
| **AMD Ryzen** | Tctl/Tdie, CCD temps, per-core clocks, load, threads, power | Dual-CCD X3D parts show V-Cache and Compute groups |
| **GPUs** | NVIDIA, AMD, Intel Arc | Fan control on NVIDIA and AMD |
| **Motherboards** | Most ASUS, MSI, Gigabyte and ASRock boards | Fans and board sensors depend on [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) support for the board's sensor chip |
| **RAM** | Usage, DDR5 DIMM temperatures | DIMM temperatures need sticks with a thermal sensor |

Not supported: Windows on ARM, Windows 7 / 8.1, 32-bit Windows, Linux and macOS.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| **Fans missing** | MSI Center, Armoury Crate, HWiNFO, AIDA64 and similar apps lock the fan chip. Close them and restart Vexis. |
| **No per-core data** | Open **Security → Install / Repair PawnIO**, then restart Vexis. |
| **A game's anti-cheat complains** | Some anti-cheat systems restrict hardware-monitoring drivers. Close Vexis while playing that game. |
| **Antivirus flags the download** | New, unsigned hardware tools sometimes trigger machine-learning detections. The source is here to inspect. |
| **Vexis crashed** | The next launch shows a banner with **Open log** and **Report on GitHub**. The log from the crashed session is kept as `%AppData%\Vexis\debug-previous.log`. |
| **Something else** | The log is at `%AppData%\Vexis\debug.log`, and live on the **Security** page. Please attach it to an [issue](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/issues). |

### About the sensor driver

Vexis reads sensors through [PawnIO](https://pawnio.eu), the signed driver used by LibreHardwareMonitor. It works with **Memory Integrity, VBS and the Vulnerable Driver Blocklist left on** — Vexis does not change any Windows security settings. (Very early Vexis builds did; if you used one, open **Security → Restore Windows Protections** and restart.)

### Security & privacy

Vexis has no telemetry, no analytics and no accounts. The only internet access is checking and downloading releases from this GitHub repository. Everything else stays on your PC.

**[SECURITY.md](SECURITY.md)** explains exactly why Vexis needs administrator rights, what it does and doesn't do with them, every network connection it makes, what it stores, and how to check a download. Found a security problem? Please report it privately, as described there.

---

## Building from source

Prerequisites: [.NET 8 SDK](https://dotnet.microsoft.com/download) and [NSIS](https://nsis.sourceforge.io/Download) (for the installer).

```powershell
git clone https://github.com/Vlottiz/Vexis-PC-monitor-and-controll.git
cd Vexis-PC-monitor-and-controll
dotnet run                      # run from an administrator terminal
dotnet test tests/Vexis.Tests   # unit tests (fan curves, sensor mapping, updates, duplicate finder...)
```

The tests also run automatically on every push and pull request (**Actions → CI**). How the code is organised, and why, is in **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

### Making a release

Releases are built automatically by GitHub Actions (`.github/workflows/release.yml`):

1. Set `<Version>` in `Pcmonitor2_0.csproj` (e.g. `2.9.0`), add a `## 2.9.0 — <date>` section with `- ` lines at the top of **`CHANGELOG.md`**, and merge into `main`. The app shows those lines on Info → Version, and the workflow uses them as the release notes (unless you write your own on the website).
2. On GitHub: **Releases → Draft a new release**, create the tag **`v2.9.0`** (a `v` plus the exact version), write the notes and **Publish**.
3. Within a few minutes the **Release** workflow runs the tests, builds `VexisHM-Setup.exe` and attaches it to that release with a `.sha256` checksum file. It also adds a **Build information** table (commit, date, toolchain, checksum) to the notes. Watch it under the **Actions** tab.
4. **Code signing (optional):** add the repository secrets `SIGN_PFX_BASE64` (your `.pfx` certificate, base64) and `SIGN_PFX_PASSWORD`, and every release is signed automatically. See [SECURITY.md](SECURITY.md#code-signing).

The workflow stops with a clear error if the tag doesn't match `<Version>`, because installed copies compare the tag with their own version — a tag like `1.0.1` or `Vexis` would never be seen as an update. You can also run it by hand from the **Actions** tab (**Run workflow**) to get a test build as a download, without releasing.

**Building the installer locally** still works: run **`build-installer.bat`** (needs the .NET 8 SDK and NSIS). It downloads PawnIO and WebView2 if needed, publishes the app and builds `VexisHM-Setup.exe`.

### Customising

- **Loading screen:** replace `splash.gif` (440 × 220 px works best) — see `SplashForm.cs`, or edit and run `assets/make_splash.py`.
- **Screenshots:** the images in `docs/screenshots/` are shown on this page. To use your own, take a screenshot of a Vexis page (`Win + Shift + S`), save it with the same file name (e.g. `gpu.png`), and replace the file on GitHub (**Add file → Upload files** into `docs/screenshots`).

---

## How Vexis is made

Vexis is designed, tested and maintained by me, [Vlottiz](https://github.com/Vlottiz). I use Claude as a development tool
for writing and debugging code, and I'm open about that.

- **I decide what gets built** and how it behaves, including the safety rules for anything touching fans or hardware.
- **Every change goes through a pull request.** It says what was designed, what was AI-assisted, how it was tested and what's still unknown.
- **Automated tests** cover the logic that doesn't need real hardware (fan curves, sensor mapping, updates, the duplicate finder). They must pass before a release is built.
- **Real hardware testing** on AMD and Intel PCs for anything that touches sensors, fans or RGB.
- **Releases are built publicly by GitHub Actions**, never on a personal PC, with a checksum and the exact source commit.

The whole process is in **[CONTRIBUTING.md](CONTRIBUTING.md)**, and how the code fits together is in **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

---

## Credits & Attributions

| Project | Use | License |
|---|---|---|
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | All hardware sensor reading | MPL-2.0 |
| [PawnIO](https://pawnio.eu) | Signed sensor driver (installed unmodified) | — |
| [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) | RGB device control (external, not bundled) | GPL-2.0 |
| [HIDAPI](https://github.com/libusb/hidapi) | HID device communication | MIT/BSD |
| [Microsoft WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) | Embedded browser for the UI | Free |
| [System.Management](https://www.nuget.org/packages/System.Management) | WMI — CPU load, RAM, Intel fallback | MIT |
| [NSIS](https://nsis.sourceforge.io) | Windows installer | zLib |
| [Claude — Anthropic](https://claude.ai) | AI assistant for accelerating development | — |

All ideas, feature design, and project direction by **[Vlottiz](https://github.com/Vlottiz)**.  
Claude assisted with implementation speed, debugging, and boilerplate. Every feature was conceived and directed by the author.

---

## Support

If Vexis is useful to you, you can support development:

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-%E2%98%95-ffd000?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/reyobu)

Found a bug or have an idea? [Open an issue](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/issues).

## License

MIT — see [LICENSE.txt](LICENSE.txt). Bundled and linked components keep their own licenses (see Credits).
