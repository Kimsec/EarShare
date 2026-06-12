# EarShare 🎧

**Play your Windows audio on several headsets at once — in sync.**

Movie night on a plane? Three people, three Bluetooth headsets, one laptop.
EarShare takes whatever your PC is playing and mirrors it to as many audio
devices as you like, in real time.

## Why you'll like it

- 🪶 **Lightweight** — one small tray app, near-zero CPU, no bloat
- 📦 **Nothing to install** — a single portable `.exe`; no .NET, no drivers, no virtual cables
- 🔊 **As many devices as you want** — Bluetooth, USB, HDMI, wired… mix freely
- 🎯 **Stays in sync** — automatic clock-drift correction keeps every headset aligned for hours
- ⚡ **Low latency** — adjustable buffer, down to 20 ms
- 🎚️ **Per-device volume and delay** — everyone gets their own loudness
- 🖱️ **Dead simple** — pick a source, add your devices, press Start. That's it.

## Get started

1. Download `EarShare.exe` from [Releases](https://github.com/Kimsec/EarShare/releases) — no installation needed
2. Run it (Windows SmartScreen may warn because the exe is unsigned: *More info → Run anyway*)
3. Choose what to mirror in **Mirror from** — or just leave it on *System default*
4. **Add device** → pick each headset, set volumes
5. Press **Start** and enjoy 🍿

Closing the window sends EarShare to the system tray and the audio keeps
playing — right-click the tray icon to stop or quit. Your devices and volumes
are remembered for next time.

Requires Windows 10/11 (64-bit). The first launch takes a few extra seconds.

## Good to know

- The source device keeps playing normally; mirrored devices run a fraction of
  a second behind it — mostly Bluetooth's own latency. In practice nobody
  notices: everyone is wearing their own headset.
- One headset behind the others? Raise the **ms** delay on the faster devices
  to line them up. Audio behind the video? Shift audio in your player instead
  (VLC: `j`/`k`).
- Crackling or dropouts? Raise the **Buffer** value a notch.
- Don't mute the source device — Windows loopback capture goes silent with it.
- Streaming to 3+ Bluetooth headsets can max out a single Bluetooth radio.
  USB Bluetooth audio transmitter dongles work great as extra outputs.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Kimsec/EarShare.git
cd EarShare
dotnet run
```

To produce the standalone single-file exe yourself:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

Built with [NAudio](https://github.com/naudio/NAudio) (WASAPI loopback capture,
one independently buffered and drift-corrected output pipeline per device).

## License

[MIT](LICENSE)
