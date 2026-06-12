# EarShare

A tiny Windows tray app that plays the current Windows system audio on **multiple output
devices at the same time** — e.g. three Bluetooth headsets watching the same movie on a
plane. A stripped-down replacement for Voicemeeter's multi-output routing, with no virtual
driver and no bloat.

- WASAPI **loopback capture** of a selectable source device ("Mirror from" dropdown,
  defaults to the Windows default output)
- Mirrors to **any number** of selected output devices, in real time
- **Independent buffer per device** — one slow/stalling device never blocks the others
- **Adaptive resampling per device** — handles different sample rates *and* continuously
  corrects clock drift between devices (±0.3 % max trim, inaudible), so headsets stay in
  sync over a whole movie
- Per-device **volume slider**, optional **delay trim** (line fast devices up with slow
  Bluetooth ones), and **remove** button; **Add device** dropdown
- Adjustable **buffer size** (20–500 ms, default 40) — trade latency against
  stutter-resistance, changes apply live
- **Start/Stop** button, minimizes to a **system tray** icon (tray menu: start/stop, quit)
- Window height adapts automatically to the number of devices
- Saved device list, volumes, delays, buffer and capture source (`%APPDATA%\EarShare\settings.json`)
- Low CPU (event-driven WASAPI, one resampler per device), a few MB of working set

## Requirements

- Windows 10 1903+ / Windows 11, x64
- To build: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- To run the published exe: **nothing** (self-contained)

## Build & run (development)

```powershell
cd EarShare
dotnet run
```

## Publish a standalone single-file .exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtraction=true `
  -p:EnableCompressionInSingleFile=true
```

Result: `bin\Release\net8.0-windows\win-x64\publish\EarShare.exe` — one file,
roughly 60–70 MB (it embeds the entire .NET runtime; WinForms cannot be IL-trimmed).
Copy it to any Windows 10/11 x64 machine and run it — no .NET install, no drivers,
no other dependencies.

> If the target machine has the .NET 8 Desktop Runtime anyway, you can instead publish
> framework-dependent (`--self-contained false`) for a ~1 MB exe.

### Sharing it

Sending someone the single `EarShare.exe` is all it takes — no installer, no .NET,
no drivers; settings are created automatically in `%APPDATA%\EarShare`. Two things
to expect on their machine:

- **SmartScreen**: a downloaded unsigned exe gets a blue "Windows protected your PC"
  screen on first run — *More info → Run anyway*. (Files copied via USB usually skip this.)
- The very first launch takes a few extra seconds while the bundled runtime unpacks.

## How to use it

1. Pair/connect all headsets.
2. Pick the capture source in **Mirror from** — or leave it on *System default*, which
   tracks whatever Windows is currently outputting to.
3. **Add device** → pick each headset, set volumes.
4. Press **Start**, play the movie. Close or minimize the window — it keeps running in
   the tray. Right-click the tray icon to stop or quit.

The app refuses to mirror onto the capture device itself — that would feed its own
output back into the loopback capture and build up an echo.

## Latency & sync — what to expect

- **The capture-source device plays live** (zero added delay). Mirrored devices play
  later by: ~10 ms capture granularity + the **Buffer** setting (default 40 ms) +
  ~20 ms output stage + the receiver's own latency (2.4 GHz dongles ~0.02–0.04 s;
  classic Bluetooth such as AirPods another 0.15–0.3 s). App-added total: ~70 ms at
  the default, ~50 ms at the minimum buffer.
- **Why a buffer at all?** Capture delivers audio in 10 ms bursts on one clock; each
  output asks for audio on its own clock at its own moments, Windows threads wake a
  few ms late under load, and Bluetooth links hiccup. The queue bridges those gaps —
  with no queue, every mismatch is an audible crackle. It can't be zero, but it can
  be small: 20–30 ms gives the tightest lip-sync on a healthy machine; raise it if
  you hear crackling or dropouts.
- **In practice this is fine.** Everyone wears their own headset, so nobody hears two
  copies of the audio — modest offsets between listeners are not noticeable. Mirroring
  straight from your normal output (HDMI, speakers, or one low-latency headset) works
  well; no special "silent default device" setup is needed.
- **What the app guarantees** is that each device's offset stays *constant*: clock drift
  is corrected continuously, so devices never creep further apart during a movie.
- **Bluetooth latency itself is not removable** — it lives in the BT stack and the
  headset's own buffering. AirPods lag the video even when they are simply the Windows
  default output with no mirroring involved; no software can make them earlier.
- **Optional, for perfectionists:** the per-device **ms** field adds extra delay to a
  device. You can't speed the AirPods up, but you can slow the fast devices down to
  match them, so all listeners hear in sync. Then, if lips are off for everyone, shift
  audio earlier in the player: VLC `k`/`j` (audio delay −/+), mpv `Ctrl+plus/minus`.
  Leave the delays at 0 if you don't care — defaults behave like before.
- **Loopback follows the capture device's Windows volume and mute.** If the source
  endpoint is muted or at 0 %, the mirrored signal is silent too. Keep the source at a
  normal level and set per-listener loudness with the app's sliders or on the headsets.
  (HDMI is convenient here: the TV's own volume/mute doesn't affect the Windows
  endpoint, so the capture signal stays at full level.)

### Real-world Bluetooth notes

- Make sure each headset is in its **Stereo (A2DP)** profile, not "Hands-Free" — in
  Settings → Bluetooth, or just avoid apps grabbing the headset mic. Hands-free mode
  sounds like a telephone and often shows up as a mono 8/16 kHz device.
- A single Bluetooth radio streaming **3× A2DP simultaneously** is demanding; many
  built-in adapters start stuttering at 2–3 streams. If that happens, plug-in USB
  Bluetooth *audio transmitter* dongles (the kind that pairs directly with a headset and
  shows up in Windows as a USB sound card) work perfectly with this app — each dongle is
  just another output device, and they offload the radio work.

## How it stays in sync (architecture)

```
                    WASAPI loopback capture (selected source, float32 mix format)
                                        │  capture thread: copy only
        ┌───────────────────────────────┼───────────────────────────────┐
        ▼                               ▼                               ▼
BufferedWaveProvider            BufferedWaveProvider            BufferedWaveProvider
 (per-device queue,              target fill = Buffer setting    DiscardOnOverflow
  silence-pad on underrun)        (default 40 ms) + delay         + panic clear & re-prime
        ▼                               ▼                               ▼
 channel router (if needed)      channel router                  channel router
        ▼                               ▼                               ▼
 volume (per device)             volume                          volume
        ▼                               ▼                               ▼
 drift-correcting resampler      drift-correcting resampler      drift-correcting resampler
 (WDL; ratio nudged ±0.3 %       capture rate → device rate
  to hold buffer at target)
        ▼                               ▼                               ▼
 WasapiOut (shared, event-       WasapiOut                       WasapiOut
  driven, 20 ms, own thread)
```

Every output device pulls from its own buffer on its own WASAPI thread. A feedback loop
measures each buffer's fill level every 100 ms and trims that device's resampling ratio
by up to ±0.3 % so the fill stays at its target (the Buffer setting, plus that device's
optional extra delay). Because all buffers are fed by the same capture stream and each is held at
its target fill, the devices keep a fixed relative timing and **cannot drift apart over
hours** — clock differences are absorbed as a tiny, inaudible pitch trim instead of
accumulating delay. Changing a device's delay takes effect instantly (silence is
inserted, or queued audio skipped), then the feedback loop holds the new level.

## Project structure

```
EarShare/
├── EarShare.csproj
├── Program.cs                        entry point, single-instance guard
├── Audio/
│   ├── MirrorEngine.cs               loopback capture + fan-out, add/remove while running
│   ├── OutputPipeline.cs             one independent chain per output device
│   ├── DriftCorrectingResampler.cs   WDL resampler + buffer-fill feedback loop
│   └── ChannelRouterProvider.cs      mono/stereo/N-channel mapping
├── Settings/
│   └── AppSettings.cs                persisted device list + volumes (JSON)
└── UI/
    ├── MainForm.cs                   window, device list, tray icon + menu
    ├── DeviceRow.cs                  per-device row (name, status, slider, remove)
    └── TrayIconFactory.cs            draws the tray icon at runtime (no assets)
```

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "skipped — capture source" on a device | That device is the capture source — it already plays the audio live, mirroring onto it would cause an echo loop. It's fine: whoever uses it just hears the audio directly. |
| No audio mirrored at all | Nothing is playing on the capture device, or that endpoint is muted / at 0 % volume (loopback follows it). |
| Crackling/dropouts on all devices | Buffer too small for the machine/load — raise the **Buffer** value (e.g. 40 → 100). |
| One headset crackles/stutters | Bluetooth bandwidth. Move dongle/laptop, drop to 2 BT devices, or use USB BT transmitter dongles. |
| "device lost" on a row | The device disconnected mid-run. Reconnect it, then Add it again (or Stop/Start). Others keep playing. |
| One headset noticeably behind the others | Inherent receiver latency (classic BT vs 2.4 GHz/wired). Raise the **ms** delay on the *fast* devices until they match the slow one. |
| Audio behind the video for everyone | Bluetooth + buffer latency. Shift audio earlier in the player: VLC `k` (and `j` to go back), mpv `Ctrl+plus/minus`. |
