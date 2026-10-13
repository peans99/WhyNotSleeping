<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

<h1 align="center">WhyNotSleeping</h1>

<p align="center">Find out what is keeping your Windows PC awake, or your screen on.</p>

![WhyNotSleeping in dark mode, running as administrator](docs/screenshot.png)

Your PC is meant to go to sleep after a while, and the screen to turn off, but it doesn't. WhyNotSleeping tells you why: a power plan set to "Never", an app playing audio, a driver holding the system awake, or a device (a mouse, a joystick, a remote) sending input that Windows counts as you being there.

## Download

Grab **WhyNotSleeping.exe** from the [latest release](https://github.com/peans99/WhyNotSleeping/releases/latest) and double-click it. There's nothing to install or unpack, and you don't need .NET.

- It asks for **administrator rights** (the UAC prompt). Only an administrator can see *which* apps and drivers are keeping Windows awake. If you say no, it still runs and shows your power settings, the screen state and your input devices.
- Windows may say the publisher is unknown because the executable is not code signed. Choose **More info**, then **Run anyway**.
- It follows your Windows light/dark setting.

**WhyNotSleepingCli.exe** is the same monitor in a console window, for anyone who'd rather have that (see [Console version](#console-version)).

## What it shows

Two verdicts at the top, **Sleep** and **Screen**, each saying in plain words whether something is blocking it and what. Below them:

- **Your power plan**: when the PC sleeps and the screen turns off ("Never" is the most common culprit), whether you're plugged in, how long you've been idle, and whether the screen is actually on, off or dimmed.
- **Keeping it awake**: the apps, services and drivers asking Windows to stay awake (audio streams, video playback, downloads, Windows Update...) and how long each has been holding on. Right-click an app to ignore its requests or open its folder.
- **Input devices**: every mouse, keyboard, game controller, touchpad, touch screen, pen, media-key remote and power button that sent input, by name (e.g. "Razer Basilisk V3"), and which ones actually reset the idle timer. Controllers with jittery axes and software-injected input (remote desktop, mouse jigglers) are flagged.
- **Event log**: requests appearing and disappearing, power changes, the screen turning off and on (and how late that was compared to the timeout), input after a quiet spell, and gaps where the PC was actually asleep. It's also saved to `%LOCALAPPDATA%\WhyNotSleeping\whynotsleeping.log`, so you can leave it running overnight and check in the morning what woke the screen at 3am.
- **Ignored apps**: apps whose requests Windows has been told to disregard, with a button to stop ignoring them.

<details>
<summary>Light mode</summary>

![WhyNotSleeping in light mode, without administrator rights](docs/screenshot-light.png)

</details>

## Fixing common causes

| What you see | What to do |
|---|---|
| Sleep after: **Never** | Click **Power & sleep settings** and pick a time under *Screen, sleep & hibernate timeouts*. |
| Screen off after: **Never** | Same place, the screen timeout. |
| An app is *Keeping the screen on* | Usually a video player or a browser tab playing video. Close or pause it. |
| An app is *Keeping the PC awake* | Often audio (a muted or forgotten stream, a voice chat app). Close it, or right-click it and choose **Ignore this app** if you never want it to keep the PC awake. |
| A driver says *An audio stream is currently in use* | Something is playing sound, even silence. Find the app playing audio and close it. |
| A **controller, joystick or HOTAS** is resetting the idle timer | See below. |

### A controller, joystick or HOTAS keeps the screen on

Windows doesn't usually count game-controller input as you being there, but some devices get counted anyway:

- **Noisy axes.** A throttle, stick, wheel or pedal whose analog axis jitters by a hair sends input all the time, even when nobody touches it. Recalibrate it, or add a small deadzone in the vendor's software (VKB DevCfg, VPC Configurator, Thrustmaster TARGET, Logitech G HUB...).
- **Keyboard or mouse modes.** Many HOTAS and button boxes also show up as a keyboard or mouse, or their software (TARGET, Joystick Gremlin, Steam Input) turns axes and buttons into key presses. That *is* keyboard input as far as Windows can tell. Look in **Input devices** for a keyboard or mouse with your stick's name, and check the device's software for mappings that send keys or mouse movement.
- **When nothing else helps,** unplug it (or use a USB hub with power switches) when you're not playing, or disable it in Device Manager and re-enable it before you fly.

The **Input devices** tab shows which device reset the idle timer and how often in the last minute, and the verdict cards name it, so you can check a fix worked.

## Console version

```text
WhyNotSleepingCli [options]
  -i, --interval <sec>  Refresh interval (default 5)
      --once            Print a single snapshot and exit
      --log <file>      Event log file (default .\whynotsleeping.log)
      --no-log          Don't write an event log
      --no-elevate      Don't relaunch as administrator (power requests won't be visible)

Keys: [q]/[Esc] quit, [r] refresh now.
```

Useful command-line equivalents of the fixes above (run as administrator):

```powershell
powercfg /change standby-timeout-ac 30                    # sleep after 30 minutes on AC
powercfg /change monitor-timeout-ac 10                    # screen off after 10 minutes on AC
powercfg /requestsoverride PROCESS app.exe SYSTEM DISPLAY  # ignore an app's requests
powercfg /requestsoverride PROCESS app.exe                 # stop ignoring it
```

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet run --project src/WhyNotSleeping.Gui     # the window
dotnet run --project src/WhyNotSleeping.Cli     # the console monitor
./build.ps1                                     # both single-file executables into dist\
```

The solution has three projects. `WhyNotSleeping.Core` holds the monitoring: power settings, `powercfg` requests and overrides, display-state notifications, raw input, and the verdicts. `WhyNotSleeping.Gui` (WinForms) and `WhyNotSleeping.Cli` are two front ends over it.

### Releasing

1. Bump `<Version>` in `Directory.Build.props` and add a `### <version>` section under [Release notes](#release-notes). CI fails a pull request that changes `src/` without both, unless a commit says why it doesn't need to with a `No-version-bump: <reason>` trailer.
2. Merge, then tag the merge commit: `git tag -a v1.0.1 -m "..." && git push origin v1.0.1`.

The release workflow refuses a tag that disagrees with `Directory.Build.props`. It builds both executables, smoke-tests the console tool, scans the files with Microsoft Defender, and publishes them with that version's notes.

## Release notes

### 1.0.0

- First release: a window (`WhyNotSleeping.exe`) and a console monitor (`WhyNotSleepingCli.exe`), both single self-contained executables.
- Verdicts for sleep and the screen: power plan timeouts, power requests, display state, and input devices that reset the idle timer.
- Ignore an app's power requests from the window, and stop ignoring it again.
- Follows the Windows light/dark setting.

## License

[Apache License 2.0](LICENSE)
