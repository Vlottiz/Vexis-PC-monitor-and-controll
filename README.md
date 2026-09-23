# Vexis

> A real-time hardware monitoring dashboard for Windows — built with C# + WebView2 + pure HTML/CSS/JS.

[![Release](https://img.shields.io/github/v/release/Vlottiz/vexis?style=flat-square&color=ffcc00)](https://github.com/Vlottiz/vexis/releases/latest) [![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](https://github.com/Vlottiz/vexis/blob/main/LICENSE) [![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey?style=flat-square&logo=windows)](https://github.com/Vlottiz/vexis/blob/main) [![Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-reyobu-ffd000?style=flat-square&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/reyobu)

---

## Why Vexis?

Vexis was built to combine hardware monitoring, fan control, and RGB control into one lightweight application with a modern UI.

Unlike traditional monitoring tools, Vexis offers:

- Fully customizable themes with live color editing
- Native fan curve editor with hysteresis and minimum speed
- RGB control via OpenRGB (500+ devices) with auto-launch
- Multi-window dashboards — pop any page to a separate monitor
- Simple installer with automatic updates

---

## Features

| Feature | Details |
|---|---|
| **CPU Monitoring** | Per-core clock speeds, temperatures, CCD temps (AMD), package power |
| **GPU Monitoring** | Dedicated GPU tab: load (core, memory controller, video, bus), core / hot-spot / memory temps, clocks, power, VRAM, fans, PCIe traffic, FPS, history charts and every raw sensor |
| **Fan Control** | Auto / Manual / Custom curve editor with hysteresis and minimum speed |
| **Temperature History** | Min / Max / Avg for every thermal sensor on the system |
| **RGB Control** | OpenRGB integration — auto-launch, master + per-device brightness, hardware modes, GUI toggle |
| **Multi-window** | Pop out any page to a separate window |
| **Themes** | 8 built-in color presets + full per-color customization + 3 saved profiles |
| **Nav Scaling** | Separate text size sliders for UI, data values, and nav panel |
| **Hardware Auto-detect** | CPU, GPU, RAM, socket type all detected and displayed automatically |
| **AMD Full Support** | Per-core clocks, CCD0/CCD1 temps, Ryzen SMU sensors |
| **Intel Support** | All-core clock via Windows Performance Counters |
| **Auto-updater** | Checks GitHub releases on startup |
| **Start with Windows** | Optional — launches minimized to the tray at sign-in (no UAC prompt) |
| **Temperature Alerts** | Tray notification when CPU or GPU passes a limit you set |
| **Collapsible Sections** | Click any section header to fold it away — remembered per page |

---

## Screenshots

> *Coming soon — drop screenshots in `/docs/screenshots/` and update these links*

| Home | Performance | Fan Control |
|---|---|---|
| ![Home](docs/screenshots/home.png) | ![Performance](docs/screenshots/performance.png) | ![Fans](docs/screenshots/fans.png) |

| Temperatures | RGB | Settings |
|---|---|---|
| ![Temps](docs/screenshots/temps.png) | ![RGB](docs/screenshots/rgb.png) | ![Settings](docs/screenshots/settings.png) |

---

## Quick Start

1. Download the [latest release](https://github.com/Vlottiz/vexis/releases/latest)
2. Run the installer
3. Launch Vexis as Administrator
4. Monitor your system in real time

---

## Installation

### Option A — Installer (Recommended)

1. Download **`Vexis-Setup.exe`** from the [latest release](https://github.com/Vlottiz/vexis/releases/latest)
2. Run it — click Next → Next → Install → Finish
3. Vexis launches automatically

The installer:
- Places the app in `Program Files\Vexis`
- Creates a Desktop and Start Menu shortcut
- Adds a Windows Defender exclusion to prevent false positives from low-level hardware monitoring components
- Appears in Add/Remove Programs for clean uninstall

### Option B — Build it yourself

See [Building from Source](#building-from-source).

---

## Requirements

- **Windows 10 or 11, 64-bit (x64)** — Intel or AMD processor
- **Administrator rights** — Vexis asks for them when it starts (needed for hardware sensors)
- **No .NET install needed** — the runtime is bundled
- **WebView2 Runtime** — built into Windows 11; the installer adds it on Windows 10 if missing (needs internet once)
- **PawnIO sensor driver** — installed by the installer

## Compatibility

| | Works | Notes |
|---|---|---|
| **Intel CPUs** | Temps, per-core clocks & load, power | 12th gen+ P-cores and E-cores shown separately |
| **AMD Ryzen** | Tctl/Tdie, CCD temps, per-core clocks & load, power | Dual-CCD X3D parts show V-Cache / Compute groups |
| **GPUs** | NVIDIA, AMD, Intel Arc | Temp, clock, load, power, VRAM, fan |
| **Motherboards** | Most ASUS, MSI, Gigabyte, ASRock boards | Fans and board temps depend on [LibreHardwareMonitor support](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) for the SuperIO chip |
| **RAM** | Usage, DDR5 DIMM temps | DIMM temps need sticks with a thermal sensor |

**Not supported**
- **Windows on ARM** (Snapdragon laptops) — the sensor driver can't run there.
- **Windows 7 / 8.1**, 32-bit Windows.

**Known limitations**
- **Fans missing?** Motherboard fan chips are shared. If **MSI Center, HWiNFO, AIDA64, Armoury Crate** or similar is running, Vexis may not be able to read them — close those apps and restart Vexis.
- **First download warnings.** Vexis isn't code-signed yet, so Windows SmartScreen may show *"Windows protected your PC"* → **More info → Run anyway**. Antivirus machine-learning scanners can also flag new, unsigned hardware tools.
- **Anti-cheat.** Some game anti-cheat systems restrict kernel drivers used by hardware monitors. If a game complains, close Vexis while playing.

### Sensor driver (PawnIO)

Per-core temperatures and clocks, motherboard fan sensors and DIMM temperatures are read through
[PawnIO](https://pawnio.eu), the driver used by LibreHardwareMonitor 0.9.6+. PawnIO is signed and
works with **Memory Integrity, VBS and the Vulnerable Driver Blocklist left ON** — Vexis does not
change any Windows security settings.

Older Vexis versions used WinRing0 and switched those protections off. If you ran one of them, open
**Security** in Vexis and click **Restore Windows Protections**, then restart Windows.

If CPU data shows as unavailable: **Security → Install / Repair PawnIO**, then restart Vexis.

---

## Fan Curve Editor

The fan curve editor supports:

- **Click** the graph to add a point (up to 8)
- **Drag** points to adjust
- **Right-click** a point to delete it
- **Temp source** — drive the curve from CPU, GPU, CCD0, or CCD1
- **Hysteresis** — prevents fan flutter by requiring the temperature to drop N°C below a threshold before reducing speed (0–10°C, default 5°C)
- **Minimum Speed** — sets a floor so fans never drop below a set percentage even at idle (0–50%)
- **Save** persists the curve — it activates automatically on next launch
- **Health Check** — ramps the fan through 30/60/100% and reports if it's responding correctly

---

## RGB Control

RGB requires [OpenRGB](https://openrgb.org) to be installed:

1. Install OpenRGB from [openrgb.org](https://openrgb.org)
2. Open Vexis → RGB tab
3. Vexis auto-launches OpenRGB with the SDK server enabled

**Features:**
- **Auto-launch** — OpenRGB starts automatically when you open the RGB page
- **Color persistence** — colors are committed to hardware firmware so they survive animations and reconnects
- **GUI button** — click ⚙ GUI to open the full OpenRGB interface for advanced configuration, then close it to return to SDK server mode
- **Quick Colors** — one-click presets applied to all devices simultaneously
- **Per-device control** — individual color pickers and per-LED support for supported devices
- **Keep-alive** — colors are re-sent every 2 seconds to prevent hardware animations from reverting

Supports 500+ devices including RAM (ENE/Corsair/G.Skill), GPUs (AMD/Nvidia), keyboards, Corsair iCUE hubs, and more.

### MSI Motherboard ARGB Headers

For MSI motherboards (X870E, B850, Z890 series), OpenRGB nightly builds include direct ARGB header support (JAF, JARGB 1/2/3). Use the [OpenRGB nightly pipeline build](https://gitlab.com/CalcProgrammer1/OpenRGB/-/pipelines) and run it as Administrator to enable PawnIO driver access.

> If using the nightly, close MSI Center completely — the two apps will conflict for HID device access.

---

## Theme System

Vexis ships with 8 built-in presets (AMBER, OCEAN, MATRIX, GHOST, RUBY, VIOLET, ARCTIC, SOLAR) plus full per-color customization.

**Three text scaling sliders** in the System tab:
- **Page UI Font** — scales labels, headers, and UI text across all pages
- **Data Text** — scales sensor readings (temperatures, RPM, clocks)
- **Nav Panel Text** — scales the navigation sidebar independently

All settings persist across sessions.

---

## Building from Source

Prerequisites (one-time):
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [NSIS](https://nsis.sourceforge.io/Download) — builds the installer `.exe`

```bash
git clone https://github.com/Vlottiz/vexis.git
cd vexis

# Run in development (admin PowerShell)
dotnet run
```

### Making a new installer after changes

1. Bump `<Version>` in `Pcmonitor2_0.csproj` (e.g. `2.1.0` → `2.1.1`). This is the only place the
   version lives — the app, the update check and the installer all read it.
2. Double-click `build-installer.bat` (or run it from a terminal). It will:
   - download `PawnIO_setup.exe` if it is missing,
   - `dotnet publish` the app into `publish\`,
   - run NSIS to produce **`VexisHM-Setup.exe`** in the project folder.
3. Create a GitHub release tagged `v<version>` (e.g. `v2.1.1`) and attach `VexisHM-Setup.exe`.
   Installed copies will show the update badge on next launch.

Running the new installer over an existing install updates it in place (settings in
`%AppData%\Vexis` are kept).

---

## Credits & Attributions

| Project | Use | License |
|---|---|---|
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | All hardware sensor reading | MIT |
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

If Vexis has been useful to you, consider buying me a coffee:

[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-%E2%98%95-ffd000?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/reyobu)

---

## License

MIT — see [LICENSE](https://github.com/Vlottiz/vexis/blob/main/LICENSE) for details.

This project uses open source components. See the Credits section above for individual licenses.
