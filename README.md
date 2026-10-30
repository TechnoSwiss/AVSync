# AVSync
Small application to sync up audio and video for broadcast using consumer grade camears

AVSync shows a large `mm:ss.mmm` clock and a light that flashes for 100 ms at the start of every second,
with a sharp audible tick at the same moment. The minute mark gets a red flash and a lower tone. Point the
IP camera at the screen, record, and compare the flash with the tick to measure the A/V offset.

## Source and sync roles
When AVSync starts, it broadcasts on the local network (UDP port 47800) for about 1.5 seconds.

- **No other AVSync answers:** this copy becomes the **source**. Its clock is the computer's clock, and the tick plays by default.
- **A source answers:** this copy becomes a **sync** app. It shows the source's time and flash. The tick is **off** by default; press `M` to turn it on.

The sync app pings the source 10 times a second and works out the clock offset the way NTP does.
It uses the sample with the lowest round-trip time from the last 4 seconds, so network queuing
and Wi-Fi jitter have little effect. The info bar shows:
- the one-way network latency
- the best and most recent round-trip time, jitter and packet loss
- how far the source's clock is from this PC's own clock

If two PCs start at the same moment and both become sources, the one that started later switches
itself to sync within a few seconds. If a sync app loses its source for 5 seconds, it searches the
network again.

## Controls
| Key | Action |
|-----|--------|
| `F11`, `F` or double-click | Toggle full screen (`Esc` exits) |
| `M` / `S` | Tick sound on/off |
| `Up` / `Down` (`+` / `-`) | Audio offset ±1 ms (hold `Shift` for ±10 ms); `0` resets it |
| `I` | Show/hide the info bar |
| Right-click | Menu with the same options |

The audio offset is saved in `%LOCALAPPDATA%\AVSync\settings.json`. Use it to correct for this
PC's own speaker or display delay so that the tick and the flash happen together.

## Command line
```
AVSync.exe [--source] [--sync [host]] [--port N] [--fullscreen]
```
- `--source`: always run as the source.
- `--sync`: always run as sync. It keeps searching until a source appears.
- `--sync 192.168.1.20`: connect straight to that host without broadcast discovery, for networks that block broadcasts.

## Building
Requires the .NET 8 SDK or later. Open `AVSync.sln` in Visual Studio 2022, or run:
```
dotnet build AVSync.sln -c Release
```
To produce a single exe that runs on a laptop without .NET installed:
```
dotnet publish src/AVSync -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Notes
- The first time the source runs, Windows Firewall asks whether to allow AVSync. Allow it on the
  network you are using, or sync apps cannot reach it.
- The tick is streamed through WASAPI. It is placed at the exact audio sample that the sound card
  will play on the second boundary, using the device's real playback position. Unlike "play sound
  now" APIs, this does not drift with output latency.
- The displayed time is drawn one display frame ahead, timed to the Windows compositor's frame
  pace, so the digits on screen show the time at which they actually appear.
