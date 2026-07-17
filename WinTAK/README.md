<p align="center">
  <img src="assets/Large.png" width="120" alt="ICU VideoStreamer"/>
</p>

# ICU VideoStreamer — WinTAK Plugin

Part of the TAK-PluginSuite-ICU_VideoStreamer suite. A WinTAK plugin that streams a
desktop camera (or the screen) into the TAK ecosystem with a **UAS-style operator HUD**,
an embedded MISB ST 0601 **KLV** metadata track, and an auto-updating **sensor marker**
(FOV cone + video link) so other TAK clients can tap the marker and open the stream.

This plugin mirrors the feature set of the suite's ATAK ICU VideoStreamer plugin
(`../ATAK`): the same operator pane, generic push destinations, sensor marker, and
quick-action bar, re-implemented for WinTAK (WPF / .NET Framework 4.8 / MEF).

## Features

- **UAS-style HUD** — docked status row (live dot · status · destination badge · settings)
  with the video filling the pane and a right-edge **Broadcast / Record / Snapshot**
  quick-action column.
- **Generic push destinations** — `Local Area Network (this device)` publishes an MPEG-TS
  **UDP multicast** (`239.2.3.1:6969`); `Media Server` pushes **RTMP / RTSP / SRT** to
  MediaMTX / TAK Restreamer. The status-row badge shows `LAN` or `SERVER → host`.
- **Start/Stop confirmations** — a dialog summarizes the destination before going live and
  before dropping the feed.
- **Sensor marker** — a `b-m-p-s-p-loc` CoT with an FOV cone and the reachable stream URL is
  dropped at the operator position while broadcasting, refreshed every 5 s, and expired on
  stop (via WinTAK's native CoT pipeline).
- **Snapshot → marker** — capture the current frame to a PNG
  (`Documents\ICU-VideoStreamer\snapshots`) and drop a spot marker at the current position.
- **KLV metadata** — MISB ST 0601 (heading / elevation / FOV / position) muxed into the
  MPEG-TS on KLV-capable destinations (LAN/UDP and SRT). RTMP/RTSP publish cannot carry KLV
  from FFmpeg, so those advertise video only.
- **WinTAK GPS integration** — position comes from WinTAK's location service; a manual
  override is available in the control drawer.
- **Codecs / audio / recording** — H.264 / H.265 / AV1, AAC / OPUS / G.711, optional local
  MP4 recording alongside the stream.

The camera picker, GPS readout, sensor heading/elevation dials, and manual-location entry
live in a collapsible **control drawer** (the ⧉ button in the status row) so the default
view matches the ATAK operator pane.

## Requirements

- WinTAK 5.6.x (SDK 5.6.0.151)
- FFmpeg — the bundled `ffmpeg\ffmpeg.exe` is used by default; override in Settings → Encoding.

## Installation

### Dev build (auto-deploys on build)

1. Copy the WinTAK SDK DLLs from `D:\Apps\` into `libs\` (see `ICUVideoStreamer.csproj`).
2. Place `ffmpeg.exe` in `ffmpeg\` (gyan.dev essentials build).
3. Open `ICUVideoStreamer.sln` in Visual Studio (Debug | x64).
4. Build — the DLL + PDB + `ffmpeg\` are copied to `D:\Apps\Plugins\` automatically.
5. Restart WinTAK — **ICU VideoStreamer** appears in Home → VISTA Tools.

### Release (.wpk)

Build the Release | x64 configuration, then install the generated `.wpk` via WinTAK's
Plugin Manager.

## Settings

| Tab | What to configure |
|-----|------------------|
| **Broadcast** | Alias, destination type (LAN / media server), push protocol, server address / port / stream path, credentials, resolution, frame rate, bitrate, video rotation |
| **Encoding** | Video codec, audio device / codec / bitrate / mute, local recording folder, FFmpeg path |
| **Sensor / KLV** | Sensor name (KLV tag 9), horizontal / vertical FOV |

Callsign, UID, and the TAK server connection are read directly from WinTAK — no separate
configuration needed.

## Architecture

Mirrors the ATAK plugin's package layout (`capture` / `serve` / `share` / `cot`):

```
VideoStreamModule.cs        MEF entry point (IModule)
VideoStreamButton.cs        Home ribbon button (VISTA Tools group)
VideoStreamDockPane.cs      Operator-pane view model (ILocationService + ICotMessageSender)
VideoStreamView.xaml        UAS-style HUD
SettingsWindow.xaml         Broadcast / Encoding / Sensor settings dialog
Capture/
  EncoderConfig.cs          Resolution / fps / bitrate / rotation
  CameraPreviewService.cs   FFmpeg rawvideo preview pump
Serve/
  MediaServerConfig.cs      Destination (LAN/SERVER) + push protocol + URLs
  StreamingService.cs       FFmpeg subprocess + DirectShow enumeration
Share/
  StreamSensorMarker.cs     Sensor-marker CoT dispatch + refresh + expiry
  SnapshotMarker.cs         Frame → PNG + spot marker
Cot/
  CotBuilder.cs             Sensor-marker + spot-marker CoT XML
Klv/
  KlvService.cs             MISB ST 0601 packet builder (named-pipe server)
  TsWriter.cs               Minimal MPEG-TS muxer (KLVA registration descriptor)
Models/
  AppSettings.cs            JSON persistence (%AppData%), DPAPI-encrypted credentials
Util/
  NetworkUtils.cs           Local address resolution
```
