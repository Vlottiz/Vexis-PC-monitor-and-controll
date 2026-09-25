# Security & Privacy

Vexis reads hardware sensors and can change fan speeds and RGB lighting, so it runs as
administrator. This page explains what it does with that access, what it connects to,
what it stores, and how you can check that a download is the real thing.

Short version: **no accounts, no analytics, no telemetry.** Vexis talks to GitHub to check for
updates, and to OpenRGB on your own PC. Everything else stays on your machine.

---

## Reporting a security problem

Please **don't** open a public issue for a security problem. Use GitHub's private report instead:
**Security → Report a vulnerability** on the
[repository page](https://github.com/Vlottiz/Vexis-PC-monitor-and-controll/security).
I'll reply as soon as I can, fix it in a release and credit you if you'd like.

Only the latest release gets security fixes. The in-app updater makes it easy to stay on it.

---

## Why Vexis needs administrator rights

Windows only allows hardware-level access for elevated programs. Vexis asks for administrator
once, when it starts (`requireAdministrator` in `app.manifest`), because it needs to:

| What | Why |
|---|---|
| Read CPU, motherboard, memory and drive sensors | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) reads temperatures, clocks and power from the CPU and the board's sensor chip, through the signed PawnIO driver |
| Set fan speeds | Writing fan speeds to the motherboard's sensor chip or the graphics driver is a hardware write |
| Install or repair the PawnIO sensor driver | Runs the bundled, unmodified `PawnIO_setup.exe` if the driver is missing or broken (checked at every launch) |
| Start with Windows (optional, off by default) | Creates a Task Scheduler task named `Vexis` that starts Vexis at sign-in without a UAC prompt. Turning the switch off, or **Reset to factory settings**, deletes it |
| End task (Processes page) | Ending another program's process. Windows-critical processes are refused |
| Duplicate finder (only when you start a scan) | Reads the folders you pick and hashes their files to compare contents. Anything you remove goes to the Recycle Bin |

### What Vexis does not do

- It **doesn't change Windows security settings.** Memory Integrity (HVCI), VBS and the Microsoft
  Vulnerable Driver Blocklist all stay on. PawnIO is a signed driver that works with them.
  (Some very early builds did switch these off. **Security → Restore Windows Protections** turns
  them back on; that's the only place Vexis writes those settings.)
- It **doesn't install anything in the background** except the PawnIO driver above and updates
  you agreed to (the **Update now** button, or **Install updates automatically** if you switched
  that on).
- It **doesn't send your data anywhere.** There's no telemetry, crash reporting service, analytics
  or ads.
- It **doesn't read your files** unless you start a duplicate-finder scan, and then only the
  folders you picked. The results never leave your PC.
- It **doesn't need an account** or an internet connection. Offline, everything works except the
  update check.

---

## Network connections

| Destination | When | What is sent |
|---|---|---|
| `api.github.com` (this repository's latest release) | At launch, and when you press **Check again** on Info | A normal HTTPS request with the user agent `Vexis/<version>`. GitHub sees your IP address, like any web request |
| `github.com` and GitHub's download servers | Only when an update is installed | Downloads the installer and its `.sha256` checksum file |
| `127.0.0.1:6742` (OpenRGB SDK) | While the RGB page is connected | Local only: device list and colour commands to the OpenRGB server on your own PC |
| Your web browser | Only when you click a link (GitHub, Buy Me a Coffee, **Report on GitHub**) | Opens the page in your normal browser. The crash helper's **Report on GitHub** fills in an issue form, and you decide whether to submit it |

The interface is local HTML served by WebView2 from the install folder (`https://vexis.local/` is a
virtual address that never touches the network). The WebView2 runtime is part of Windows and
updates itself through Microsoft.

---

## Data stored on your PC

| Location | What |
|---|---|
| `%AppData%\Vexis\config.json` | Your settings: theme, fan curves and profile, alert limits, saved RGB looks, window position |
| `%AppData%\Vexis\WebView2Cache\` | Page storage for the interface (text sizes, menu order and similar) |
| `%AppData%\Vexis\debug.log`, `debug-previous.log`, `crash.txt` | Logs for troubleshooting. They stay on your PC and are only shared if you attach them to an issue |
| `Documents\Vexis Logs\` | CSV recordings, only when you record |
| `C:\Program Files\Vexis\` | The program itself |

**Info → Reset to factory settings** deletes the settings and page storage (recordings and logs
are kept). Uninstalling removes the program; delete `%AppData%\Vexis` too for a completely clean removal.

---

## Checking a download

Each release built by GitHub Actions ([`release.yml`](.github/workflows/release.yml)) comes with:

- **A SHA-256 checksum.** It's in the release notes and in `VexisHM-Setup.exe.sha256`. To check your
  download in PowerShell:
  ```powershell
  Get-FileHash .\VexisHM-Setup.exe
  ```
  The in-app updater checks this automatically, and won't run an installer that doesn't match.
- **Build information.** The exact source commit, build date, toolchain, and a link to the public
  Actions run that built it. The unit tests have to pass before anything is released.
- **The commit inside the app.** **Info → About → Build** shows the commit the copy you're running
  was built from.

### Code signing

The release workflow signs `Vexis.exe` and the installer automatically once a code-signing
certificate is added as repository secrets (`SIGN_PFX_BASE64` and `SIGN_PFX_PASSWORD`). Each
release's **Build information** says whether that build is signed.

Until then, releases are unsigned, and Windows SmartScreen may warn about a new program from an
unknown publisher. The checksum and build information above are the way to check an unsigned download.

---

## Third-party components

| Component | Use | License |
|---|---|---|
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (+ HidSharp, RAMSPDToolkit) | Sensor reading, fan control | MPL-2.0 |
| [PawnIO](https://pawnio.eu) | Signed kernel driver for sensor access, installed unmodified | see pawnio.eu |
| [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | Renders the interface | Microsoft |
| [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) | RGB control. Not bundled; you install it yourself | GPL-2.0 |
| [HIDAPI](https://github.com/libusb/hidapi) | HID device access | BSD-style |
| [Newtonsoft.Json](https://www.newtonsoft.com/json), System.Management, .NET 8 | Runtime libraries | MIT |
| [NSIS](https://nsis.sourceforge.io) | Installer | zlib |

Dependencies are NuGet packages pinned to exact versions in `Pcmonitor2_0.csproj`, so a build
always uses the same ones.
