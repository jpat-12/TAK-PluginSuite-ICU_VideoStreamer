<p align="center">
  <img src="Assets/Large.png" width="120" alt="Video Stream Plugin"/>
</p>

# ICU VideoStreamer — WinTAK Plugin

Part of the TAK-PluginSuite-ICU_VideoStreamer suite. A WinTAK plugin for live video streaming with automatic CoT video announcement, GPS-linked position, and OpenTAK Server integration.

## Features

- **FFmpeg-based streaming** — RTMP, RTMPS, RTSP, RTSPS, SRT, UDP
- **WinTAK GPS integration** — position updates automatically from WinTAK's location service; no separate GPS setup
- **CoT video announcement** — sends a `b-i-v` event through WinTAK's existing TAK server connection when streaming starts, using your WinTAK callsign and UID
- **Screen share mode** — capture your desktop instead of a camera
- **Optional recording** — save a local copy while streaming
- **OpenTAK Server** — register/unregister the stream via REST API

## Requirements

- WinTAK 5.6.x (SDK 5.6.0.151)
- [FFmpeg](https://ffmpeg.org/download.html) — set path in Settings → Stream

## Installation

### Dev build (auto-deploys on build)

1. Copy WinTAK DLLs from `D:\Apps\` into `libs\` (see list in `VideoStreamPlugin.csproj`)
2. Open `VideoStreamPlugin.sln` in Visual Studio 2022
3. Build (Debug) — the DLL is copied to `D:\Apps\Plugins\` automatically
4. Restart WinTAK — **Video Stream** appears in Home → VISTA Tools

### Release (.wpk)

Build Release configuration, then install the generated `.wpk` via WinTAK's Plugin Manager.

## Settings

| Tab | What to configure |
|-----|------------------|
| **Stream** | Protocol, URL, stream key, credentials, FFmpeg path |
| **Video** | Codec, resolution, frame rate, bitrate, recording |
| **Audio** | Device, codec, bitrate |
| **OpenTAK** | Server URL, bearer token |

Callsign, team, role, and TAK server connection are read directly from WinTAK — no separate configuration needed.

## Architecture

```
VideoStreamModule.cs       MEF entry point
VideoStreamButton.cs       Home ribbon button (VISTA Tools group)
VideoStreamDockPane.cs     ViewModel — imports ILocationService + ICotMessageSender
VideoStreamView.xaml       Dock pane UI
SettingsWindow.xaml        4-tab settings dialog
Services/
  StreamingService.cs      FFmpeg subprocess management
  OpenTAKServerClient.cs   REST API client
Cot/
  CotBuilder.cs            Builds b-i-v video CoT XML
```
