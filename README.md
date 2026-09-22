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
| **GPU Monitoring** | Temperature, clock speed, voltage, power draw, VRAM |
| **Fan Control** | Auto / Manual / Custom curve editor with hysteresis and minimum speed |
| **Temperature History** | Min / Max / Avg for every thermal sensor on the system |
| **RGB Control** | OpenRGB integration — auto-launch, color persistence, GUI toggle |
| **Multi-window** | Pop out any page to a separate window |
| **Themes** | 8 built-in color presets + full per-color customization + 3 saved profiles |
| **Nav Scaling** | Separate text size sliders for UI, data values, and nav panel |
| **Hardware Auto-detect** | CPU, GPU, RAM, socket type all detected and displayed automatically |
| **AMD Full Support** | Per-core clocks, CCD0/CCD1 temps, Ryzen SMU sensors |
| **Intel Support** | All-core clock via Windows Performance Counters |
| **Auto-updater** | Checks GitHub releases on startup |

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

### Option B — Manual

1. Download and extract `Vexis-portable.zip` from releases
2. Run `Pcmonitor2.0.exe` as Administrator

---

## Requirements

- **Windows 10 or 11** (64-bit)
- **Administrator privileges** — required for hardware sensor access (the app auto-elevates)
- **Internet** on first launch — downloads the hardware sensor driver (~14KB)
- **WebView2** — pre-installed on Windows 11; Windows 10 users may need to install it from [Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

### Intel CPU Note

Intel CPUs require a kernel-mode driver (`WinRing0`) to read per-core temperatures and clock speeds. On most modern Windows 11 systems this driver is blocked by the **Vulnerable Driver Blocklist** (a Windows security feature).

**What you get on Intel without the driver:**
- ✅ All-core average clock speed (accurate, reflects Turbo Boost)
- ✅ CPU load percentage
- ✅ GPU temperature, clock, power
- ✅ RAM, fans, all other sensors
- ❌ Per-core individual temps/clocks

**To unlock full Intel sensor support:**
> Windows Security → Device Security → Core Isolation → Microsoft Vulnerable Driver Blocklist → **OFF** → Restart

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

```bash
# Prerequisites:
# - .NET 8 SDK: https://dotnet.microsoft.com/download
# - NSIS (for installer): https://nsis.sourceforge.io

git clone https://github.com/Vlottiz/vexis.git
cd vexis

# Run in development (admin PowerShell)
dotnet run

# Build installer
.\build-installer.bat
```

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
