# How Vexis is made, and how to contribute

## Who does what

Vexis is designed and maintained by me, [Vlottiz](https://github.com/Vlottiz). I use
[Claude](https://claude.ai) (Anthropic's AI assistant) as a development tool, and I'd rather be upfront
about exactly how.

**What I do:**

- decide what gets built and how it should behave, from my own ideas and from what people report
- set the rules for anything that touches hardware (fan safety limits, what the app is and isn't allowed to change on Windows)
- test on real hardware: my own PC plus friends' and family's machines (AMD and Intel, different boards and GPUs)
- review every change before it's merged and released, and fix what comes back from users

**What Claude does:** it writes much of the code to my spec and helps with debugging, refactoring,
tests and documentation.

It doesn't have the final say. A change it writes goes through the same pull request, tests and
hardware testing as anything else, and if something breaks, that's on me to fix.

## How a change gets in

1. **Behaviour first.** An issue or idea describes what should happen, including edge cases.
   For hardware, that means things like "what if the sensor disappears?" or "what if the GPU overheats while on a manual speed?"
2. **Implementation** on a branch, by hand or with Claude.
3. **Pull request.** The template asks what changed and why, what was AI-assisted, how it was
   tested, and what's still unknown. Nothing goes straight to `main`.
4. **Automated checks.** The CI workflow builds the app and runs the unit tests (`tests/Vexis.Tests`).
5. **Real hardware.** Anything touching sensors, fans, RGB, drivers or the installer is tried on an
   actual PC before release. The PR says which, and says so if it hasn't been yet.
6. **Merge and release.** Releases are built by GitHub Actions from a tagged commit, never on a personal PC.
   They include a SHA-256 checksum and build information. See [SECURITY.md](SECURITY.md).

## Building and testing

You need Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build Pcmonitor2_0.csproj            # build the app
dotnet test tests/Vexis.Tests               # run the unit tests
.\build-installer.bat                       # full installer (needs NSIS)
```

The tests cover the logic that doesn't need real hardware:

- fan curves, hysteresis and minimum speeds
- fan profile presets (e.g. pumps never below 60 %, louder presets never quieter)
- how CPU per-thread loads map to cores, for AMD, Intel hybrid and ungrouped sensor names
- update version handling and SHA-256 checksum files
- the duplicate finder (only truly identical files are grouped)
- refusing path tricks when opening recordings

If you change any of these areas, add or update a test.

Pages (`*.html`) can be opened in a normal browser from a local web server
(`python -m http.server`) and fed sample data from the console with `receiveData({...})`.

## Reporting problems

Use the **Bug report** form on the Issues page. It asks for your hardware, Windows version, Vexis
version and logs, which is usually what's needed to reproduce a sensor or fan problem. If Vexis
crashed, the next launch offers **Report on GitHub** with the details already filled in.

For security problems, see [SECURITY.md](SECURITY.md). Please don't post them publicly.

## Code guidelines

- Anything touching hardware must fail safe: if a reading is missing, fall back or skip; never
  guess a fan speed of 0.
- Slow work (disk, network, WMI, process lists) stays off the UI thread.
- Comments explain *why* something is done, especially hardware quirks.
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) has the bigger picture.
- Keep pull requests focused. A refactor and a feature are two PRs.
