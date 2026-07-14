# WinTAK-ICU Analysis & ATAK Plugin Implementation Guide

## 1. Executive Summary

WinTAK-ICU is a Windows desktop application (WPF, C#/.NET 8) that provides live video streaming tightly integrated with the TAK (Team Awareness Kit) ecosystem. It was ported from [OpenTAK_ICU](https://github.com/brian7704/OpenTAK_ICU) (Android). This document provides a full breakdown of every feature, module, and data flow in WinTAK-ICU and maps each component to its equivalent implementation within a single unified ATAK Android plugin.

---

## 2. Repository Structure

```
WinTAK-ICU/
├── App.xaml / App.xaml.cs          # WPF application entry point
├── MainWindow.xaml / .cs           # Main UI: camera preview, stream controls, CoT status
├── Cot/
│   └── CotBuilder.cs               # Generates CoT XML (SA events + video announcements)
├── Models/
│   └── AppSettings.cs              # All settings, JSON persistence to %AppData%
├── Services/
│   ├── StreamingService.cs         # FFmpeg subprocess manager + bitrate parser
│   ├── CotService.cs               # TCP/TLS CoT client + UDP multicast sender
│   └── OpenTAKServerClient.cs      # HTTP REST client for OpenTAK Server
└── Views/
    └── SettingsWindow.xaml / .cs   # 5-tab settings dialog
```

---

## 3. Feature Breakdown

### 3.1 Video Streaming Engine (`Services/StreamingService.cs`)

#### What It Does
- Launches an FFmpeg subprocess with a dynamically built argument string
- Supports two input sources: DirectShow camera capture or GDI screen capture
- Encodes video to H.264, H.265, or AV1 and audio to AAC, OPUS, or G.711
- Outputs to one of six streaming protocols: RTMP, RTMPS, RTSP, RTSPS, SRT, or UDP multicast
- Optionally simultaneously records to MP4 while streaming
- Monitors FFmpeg stderr for real-time bitrate statistics
- Gracefully terminates FFmpeg via stdin `q` command, with `SIGKILL` fallback after 3 s

#### FFmpeg Command Structure

```
ffmpeg
  # --- Input ---
  -f dshow -i "video=<device>[:audio=<device>]"   # camera via DirectShow
  OR
  -f gdigrab -framerate 30 -i desktop              # full screen capture

  # --- Video encoding ---
  -vcodec libx264 -preset ultrafast -tune zerolatency   # H.264
  OR -vcodec libx265 -preset ultrafast -tune zerolatency   # H.265
  OR -vcodec libaom-av1 -cpu-used 8 -row-mt 1              # AV1

  -vf scale=<width>:<height>
  -r <framerate>
  -b:v <bitrate>k
  -maxrate <1.5x bitrate>k
  -bufsize <2x bitrate>k

  # --- Audio encoding ---
  -acodec aac    -b:a <bitrate>k   # AAC
  OR -acodec libopus -b:a <bitrate>k   # OPUS
  OR -acodec pcm_mulaw -b:a <bitrate>k # G.711
  OR -an                               # muted

  # --- Primary output ---
  -f flv    "<rtmp://...>"         # RTMP / RTMPS
  OR -f rtsp  "<rtsp://...>"       # RTSP / RTSPS
  OR -f mpegts "<srt://...>"       # SRT
  OR -f mpegts "udp://239.2.3.1:6969"  # UDP multicast

  # --- Optional recording output ---
  -c copy -f mp4 "<timestamp>.mp4"

  -stats -loglevel warning
```

#### Codec / Protocol Mappings

| Setting | FFmpeg flag |
|---|---|
| H264 | `-vcodec libx264 -preset ultrafast -tune zerolatency` |
| H265 | `-vcodec libx265 -preset ultrafast -tune zerolatency` |
| AV1 | `-vcodec libaom-av1 -cpu-used 8 -row-mt 1` |
| AAC | `-acodec aac` |
| OPUS | `-acodec libopus` |
| G711 | `-acodec pcm_mulaw` |
| RTMP/RTMPS | `-f flv` |
| RTSP/RTSPS | `-f rtsp` |
| SRT | `-f mpegts` |
| UDP | `-f mpegts` |

#### RTMP Credential Injection
When a username and password are configured for RTMP, credentials are injected directly into the URL:
```
rtmp://user:pass@host:port/app/streamkey
```

#### Camera/Audio Device Enumeration
```
ffmpeg -list_devices true -f dshow -i dummy
```
Output is parsed: find the `DirectShow video devices` or `DirectShow audio devices` section, then extract quoted device names line by line.

#### Bitrate Monitoring
FFmpeg writes stats to stderr in the format:
```
frame=  120 fps= 30 q=28.0 size=   1024kB time=00:00:04.00 bitrate=2048.0kbits/s
```
The service scans each line for `bitrate=…kbits` and fires a `BitrateUpdated` event.

---

### 3.2 CoT Engine (`Cot/CotBuilder.cs` + `Services/CotService.cs`)

#### 3.2.1 CotBuilder — XML Generation

**SA (Situational Awareness) Event** — `BuildSaEvent`

CoT type is determined by team color:

| Team Color | CoT Type |
|---|---|
| Cyan, Green, Blue, Magenta, Purple, Teal, Dark Blue | `a-f-G-U-C` (friendly) |
| Red, Maroon | `a-h-G-U-C` (hostile) |
| Yellow, White, Orange | `a-n-G-U-C` (neutral) |

Stale time: **now + 30 seconds**

XML structure:
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<event version="2.0" uid="<uid>" type="a-f-G-U-C" how="m-g"
       time="<utc>" start="<utc>" stale="<utc+30s>">
  <point lat="<7dp>" lon="<7dp>" hae="9999999.0" ce="9999999.0" le="9999999.0"/>
  <detail>
    <takv device="Windows PC" platform="WinTAK-ICU" os="Windows" version="1.0.0"/>
    <contact callsign="<callsign>" endpoint="*:-1:stcp"/>
    <uid Droid="<callsign>"/>
    <__group name="<team>" role="<role>"/>
    <status battery="100"/>
    <track speed="<speed>" course="<course>"/>
  </detail>
</event>
```

**Video Announcement Event** — `BuildVideoEvent`

CoT type: `b-i-v`, how: `h-e`, stale: **now + 1 hour**

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<event version="2.0" uid="<uid>-VIDEO" type="b-i-v" how="h-e"
       time="<utc>" start="<utc>" stale="<utc+1h>">
  <point lat="<7dp>" lon="<7dp>" hae="9999999.0" ce="9999999.0" le="9999999.0"/>
  <detail>
    <__video url="<streamUrl>" uid="<uid>-VIDEO"/>
    <ConnectionEntry
      networkTimeout="12000"
      uid="<uid>-VIDEO"
      path=""
      protocol="rtsp|rtmp|srt|udp"
      address="<streamUrl>"
      port="<parsed port>"
      roverPort="-1"
      rtspReliable="0"
      ignoreEmbeddedKLV="false"
      alias="<callsign>"/>
  </detail>
</event>
```

#### 3.2.2 CotService — Delivery

**TCP/TLS Connection:**
- Plain TCP: port 8087 (standard TAK Server)
- TLS: port 8089, using `SslStream` with a permissive certificate validator (accepts self-signed certs common in TAK deployments)
- Connection timeout: 5 s send, 30 s receive
- Background read loop: accumulates bytes, emits complete `</event>`-terminated messages via `MessageReceived` event
- A `SemaphoreSlim(1)` serializes all TCP writes to prevent concurrent send corruption

**UDP Multicast:**
- Group: `239.2.3.1` port `6969` (ATAK standard SA multicast)
- Each SA and video CoT is sent to both TCP and UDP when `SendMulticast` is enabled

**Send Flow:**
1. On stream start: `SendVideoAnnouncementAsync(streamUrl, lat, lon)` — one-time `b-i-v` event
2. On CoT timer tick (default 5 s): `SendSaAsync(lat, lon, alt, speed, course)` — recurring `a-f-G-U-C` event

---

### 3.3 OpenTAK Server Client (`Services/OpenTAKServerClient.cs`)

HTTP REST client for integration with an OpenTAK Server instance:

| Operation | Method + Endpoint | Purpose |
|---|---|---|
| Register stream | `POST /api/video` | Advertise active stream with metadata |
| Deregister stream | `DELETE /api/video/{uid}` | Remove registration on stream stop |
| Health check | `GET /api/healthcheck` | Verify connectivity, return server version |

Request payload for `POST /api/video`:
```json
{
  "uid": "<device-uuid>",
  "callsign": "<callsign>",
  "url": "<streamUrl>",
  "protocol": "rtmp",
  "codec": "H264",
  "latitude": 0.0,
  "longitude": 0.0,
  "timestamp": "<utc-iso8601>"
}
```

Auth: `Authorization: Bearer <token>` header. HTTP timeout: 10 s. All failures are silent (best-effort).

---

### 3.4 Settings Model (`Models/AppSettings.cs`)

All settings are persisted as a JSON file at `%AppData%\WinTAK-ICU\settings.json`.

| Category | Field | Default |
|---|---|---|
| **Stream** | `StreamProtocol` | `RTMP` |
| | `StreamUrl` | `rtmp://localhost/live` |
| | `StreamKey` | `stream` |
| | `StreamUsername` | _(empty)_ |
| | `StreamPassword` | _(empty)_ |
| | `UseTls` | `false` |
| | `CertificatePath` | _(empty)_ |
| **Video** | `VideoDevice` | _(empty)_ |
| | `VideoCodec` | `H264` |
| | `VideoBitrate` | `2000` kbps |
| | `Resolution` | `1280x720` |
| | `FrameRate` | `30` fps |
| | `EnableRecording` | `false` |
| | `RecordingPath` | _(empty)_ |
| | `EnableScreenShare` | `false` |
| **Audio** | `AudioDevice` | _(empty)_ |
| | `AudioCodec` | `AAC` |
| | `AudioBitrate` | `128` kbps |
| | `MuteAudio` | `false` |
| **TAK** | `TakServerHost` | _(empty)_ |
| | `TakServerPort` | `8089` |
| | `TakServerTls` | `false` |
| | `CallSign` | `WinTAK-ICU` |
| | `Uid` | _(random GUID)_ |
| | `Team` | `Cyan` |
| | `Role` | `Team Member` |
| | `SendMulticast` | `true` |
| | `CotSendIntervalSeconds` | `5` |
| **OpenTAK** | `OpenTakServerUrl` | _(empty)_ |
| | `OpenTakServerToken` | _(empty)_ |
| | `ReportStreamToOpenTak` | `false` |
| **FFmpeg** | `FfmpegPath` | `ffmpeg` (PATH) |

`FullStreamUrl` computed property:
- RTMP/RTMPS: `<StreamUrl>/<StreamKey>`
- All others: `StreamUrl` as-is

---

### 3.5 Main UI (`MainWindow.xaml` + `MainWindow.xaml.cs`)

**3-row layout:**
1. **Title bar** — app name, streaming status dot + bitrate display, Settings button
2. **Content area** — camera preview (LibVLC `VideoView`) on the left; scrollable side panel on the right
3. **Status bar** — status text + live clock (1 s tick)

**Side panel controls:**
- Camera device `ComboBox` (populated async via FFmpeg dshow enumeration on startup)
- Refresh devices button
- Screen share `CheckBox` (disables the camera selector when checked; shows placeholder text in preview area)
- Stream target URL display (read-only, updates from settings)
- **Start Stream** / **Stop Stream** buttons (mutually exclusive enabled state)
- TAK Server status indicator (colored dot + text) + Connect/Disconnect button
- Location grid: Lat, Lon, Alt, CoT sent count
- Expandable manual coordinate entry (lat/lon text boxes + Apply button)

**Three timers:**

| Timer | Interval | Action |
|---|---|---|
| Clock timer | 1 s | Update time display |
| CoT timer | Configurable (default 5 s) | Send SA CoT event |
| Stream timer | 1 s | Update elapsed stream duration |

**State machine on Start:**
- Set stream dot green, update status text
- Disable Start button, enable Stop button
- Show REC badge if recording enabled
- Start stream timer and CoT timer
- Send one-time video CoT announcement
- `POST` stream to OpenTAK Server

**State machine on Stop:**
- Set stream dot red, clear bitrate
- Enable Start button, disable Stop button
- Hide REC badge
- Stop stream timer and CoT timer
- `DELETE` stream from OpenTAK Server

**Settings re-apply:** When the settings dialog is confirmed, all three services (`StreamingService`, `CotService`, `OpenTAKServerClient`) are disposed and recreated with the new settings.

---

### 3.6 Settings UI (`Views/SettingsWindow.xaml` + `.cs`)

A 5-tab `TabControl` dialog:

| Tab | Controls |
|---|---|
| **Stream** | Protocol dropdown (RTMP/RTMPS/RTSP/RTSPS/SRT/UDP), URL, Stream Key, Username, Password, FFmpeg path (with file-browse dialog) |
| **Video** | Codec dropdown, Resolution dropdown, FrameRate dropdown, Bitrate slider (with live kbps label), Recording toggle, Recording folder (with folder-browse dialog) |
| **Audio** | Codec dropdown, Bitrate dropdown, Mute toggle |
| **TAK** | Callsign, Team dropdown (12 colors), Role dropdown, Server host, Port, TLS toggle, Multicast toggle, CoT interval |
| **OpenTAK** | Server URL, Token, Enable toggle, Test Connection button (async ping → shows version or error) |

Protocol dropdown change auto-suggests a URL template when the URL box is empty or contains a default placeholder.

---

## 4. ATAK Plugin Implementation Plan

This section is a direct implementation guide for an agent to build a single unified ATAK Android plugin replicating all WinTAK-ICU features.

---

### 4.1 Platform Translation Map

| WinTAK-ICU (Windows / C#) | ATAK Plugin (Android / Java or Kotlin) |
|---|---|
| WPF `Window` + `Grid` | ATAK `DropDownReceiver` + `Fragment` layout |
| LibVLCSharp camera preview | CameraX `PreviewView` / Camera2 `SurfaceView` |
| FFmpeg subprocess (DirectShow) | `ffmpeg-kit-android` (`FFmpegKit.executeAsync`) |
| GDI screen capture | Android `MediaProjection` API + `VirtualDisplay` |
| DirectShow device enumeration | `CameraManager.getCameraIdList()` |
| `System.Net.Sockets.TcpClient` | `java.net.Socket` / `javax.net.ssl.SSLSocket` |
| `UdpClient.JoinMulticastGroup` | `java.net.MulticastSocket` |
| `System.Text.Json` settings file | Android `SharedPreferences` |
| `System.Net.Http.HttpClient` | OkHttp |
| `DispatcherTimer` | `Handler(Looper.getMainLooper()).postDelayed(...)` |
| `SslStream` self-signed bypass | Custom `X509TrustManager` returning true for all certs |
| `FolderBrowserDialog` / `OpenFileDialog` | Android storage picker (`Intent.ACTION_OPEN_DOCUMENT_TREE`) |

---

### 4.2 Plugin Project Structure

```
atak-videostreamingplugin/
├── app/
│   ├── src/main/
│   │   ├── AndroidManifest.xml
│   │   ├── java/com/example/videostreamingplugin/
│   │   │   ├── plugin/
│   │   │   │   ├── VideoStreamingLifecycle.java     # Plugin entry point (AbstractPlugin)
│   │   │   │   └── VideoStreamingDropDown.java      # Main DropDownReceiver
│   │   │   ├── cot/
│   │   │   │   └── CotBuilder.java                  # SA + video CoT XML builders
│   │   │   ├── service/
│   │   │   │   ├── StreamingService.java            # ffmpeg-kit streaming manager
│   │   │   │   ├── CotService.java                  # TCP/TLS + UDP multicast
│   │   │   │   └── OpenTAKServerClient.java         # OkHttp REST client
│   │   │   ├── model/
│   │   │   │   └── StreamSettings.java              # SharedPreferences-backed settings
│   │   │   └── ui/
│   │   │       ├── MainFragment.java                # Main streaming UI
│   │   │       └── SettingsFragment.java            # Settings UI (5 sections)
│   │   └── res/
│   │       ├── layout/
│   │       │   ├── fragment_main.xml
│   │       │   └── fragment_settings.xml
│   │       └── values/strings.xml
│   └── build.gradle
└── gradle/...
```

---

### 4.3 Module 1 — CotBuilder

Direct port of `Cot/CotBuilder.cs`. Two static methods:

```java
public class CotBuilder {

    private static final Map<String, String> TEAM_TYPES = new HashMap<>();
    static {
        // Friendly
        for (String t : new String[]{"Cyan","Green","Blue","Magenta","Purple","Teal","Dark Blue"})
            TEAM_TYPES.put(t, "a-f-G-U-C");
        // Hostile
        TEAM_TYPES.put("Red",    "a-h-G-U-C");
        TEAM_TYPES.put("Maroon", "a-h-G-U-C");
        // Neutral
        for (String t : new String[]{"Yellow","White","Orange"})
            TEAM_TYPES.put(t, "a-n-G-U-C");
    }

    public static String buildSaEvent(String uid, String callsign, String team, String role,
        double lat, double lon, double alt, double speed, double course) { ... }

    public static String buildVideoEvent(String uid, String callsign,
        String streamUrl, double lat, double lon) { ... }
}
```

Implementation notes:
- Use `android.util.Xml` or `org.w3c.dom.Document` for XML serialization
- Timestamp format: `yyyy-MM-dd'T'HH:mm:ss.SSS'Z'` in UTC (`SimpleDateFormat` with `TimeZone.getTimeZone("UTC")`)
- SA stale = now + 30 s; video stale = now + 3600 s
- For the ATAK plugin, set `platform="ATAK-ICU"` and `os="Android"` in `<takv>`
- Protocol detection for `ConnectionEntry`: check URL prefix (`rtsp`, `rtmp`, `srt`, `udp`); use `Uri.parse(url).getPort()` for port

---

### 4.4 Module 2 — CotService

Port of `Services/CotService.cs`:

```java
public class CotService {
    // Lifecycle
    public void connect(String host, int port, boolean useTls) throws IOException
    public void disconnect()
    public boolean isConnected()

    // Sending
    public void sendSa(double lat, double lon, double alt, double speed, double course)
    public void sendVideoAnnouncement(String streamUrl, double lat, double lon)

    // Callbacks
    public interface Listener {
        void onStatusChanged(String status);
        void onMessageReceived(String cotXml);
    }
    public void setListener(Listener l)
}
```

Implementation details:

**TLS:** Build `SSLContext` with a custom `TrustManager` that accepts all certificates:
```java
TrustManager[] trustAll = new TrustManager[] {
    new X509TrustManager() {
        public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        public void checkClientTrusted(X509Certificate[] c, String a) {}
        public void checkServerTrusted(X509Certificate[] c, String a) {}
    }
};
SSLContext ctx = SSLContext.getInstance("TLS");
ctx.init(null, trustAll, new SecureRandom());
Socket socket = ctx.getSocketFactory().createSocket(host, port);
```

**Read loop:** Run on `ExecutorService` background thread. Read bytes into `StringBuilder`, extract complete CoT events by detecting `</event>` boundary. Dispatch to listener on main thread via `Handler`.

**Write locking:** Use `ReentrantLock` to serialize TCP writes.

**UDP multicast:**
```java
MulticastSocket udp = new MulticastSocket();
udp.setTimeToLive(32);
byte[] data = xml.getBytes(StandardCharsets.UTF_8);
DatagramPacket pkt = new DatagramPacket(data, data.length,
    InetAddress.getByName("239.2.3.1"), 6969);
udp.send(pkt);
udp.close();
```
Requires `android.permission.CHANGE_WIFI_MULTICAST_STATE` and calling `WifiManager.MulticastLock.acquire()` before sending.

**ATAK-native alternative:** Inside ATAK, prefer `CotDispatcher.getInstance().dispatch(CotEvent)` to route CoT through ATAK's internal pipeline in addition to (or instead of) raw TCP, ensuring map SA updates work even when not connected to an external TAK server.

---

### 4.5 Module 3 — StreamingService

The most complex module. Use **ffmpeg-kit-android** as a drop-in replacement for the FFmpeg subprocess.

```java
public class StreamingService {
    public void start(StreamSettings s) throws Exception
    public void stop()
    public boolean isStreaming()
    public boolean isRecording()

    public interface Listener {
        void onBitrateUpdate(int kbps);
        void onStatusChanged(String status);
        void onStopped();
    }
}
```

**FFmpeg argument translation:**

The argument string is nearly identical to the Windows version. Key differences:

| Windows Input | Android Input |
|---|---|
| `-f dshow -i "video=<name>"` | `-f android_camera -i <camera_index>` |
| `-f gdigrab -framerate 30 -i desktop` | `MediaProjection` + `VirtualDisplay` → pipe to ffmpeg-kit input surface |

All video/audio encoding flags (`-vcodec`, `-preset`, `-b:v`, `-acodec`, etc.) and all output flags (`-f flv`, `-f rtsp`, etc.) are **identical** to the Windows version.

**ffmpeg-kit integration:**
```java
FFmpegKit.executeAsync(ffmpegArgs, session -> {
    // completion callback
    ReturnCode rc = session.getReturnCode();
    if (!ReturnCode.isSuccess(rc)) {
        listener.onStatusChanged("FFmpeg error: " + session.getFailStackTrace());
    }
    listener.onStopped();
}, logCallback -> {
    // log output (replaces stderr read loop)
}, statistics -> {
    // bitrate monitoring (replaces ParseStatsLine)
    int bitrate = (int) statistics.getBitrate();
    if (bitrate > 0) listener.onBitrateUpdate(bitrate);
});
```

Stop:
```java
FFmpegKit.cancel();  // graceful stop
```

**Camera enumeration:**
```java
CameraManager mgr = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
for (String id : mgr.getCameraIdList()) {
    CameraCharacteristics ch = mgr.getCameraCharacteristics(id);
    Integer facing = ch.get(CameraCharacteristics.LENS_FACING);
    // LENS_FACING_BACK, LENS_FACING_FRONT, LENS_FACING_EXTERNAL
    devices.add("Camera " + id + " (" + facingName(facing) + ")");
}
```

**Audio device enumeration:**
```java
AudioManager am = (AudioManager) ctx.getSystemService(Context.AUDIO_SERVICE);
AudioDeviceInfo[] inputs = am.getDevices(AudioManager.GET_DEVICES_INPUTS);
```

**Screen capture:**
```java
// 1. Request permission
Intent intent = projectionManager.createScreenCaptureIntent();
startActivityForResult(intent, REQUEST_MEDIA_PROJECTION);

// 2. In onActivityResult, create VirtualDisplay
MediaProjection projection = projectionManager.getMediaProjection(resultCode, data);
VirtualDisplay vd = projection.createVirtualDisplay("ScreenCapture",
    width, height, dpi, DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
    surface, null, null);
// 3. Pass the surface as ffmpeg-kit input
```

---

### 4.6 Module 4 — StreamSettings

Port of `Models/AppSettings.cs` using `SharedPreferences`:

```java
public class StreamSettings {
    private final SharedPreferences prefs;

    public StreamSettings(Context ctx) {
        prefs = ctx.getSharedPreferences("video_streaming_plugin", Context.MODE_PRIVATE);
    }

    // All getters/setters matching AppSettings fields.
    // UID: generate UUID once and persist.
    public String getUid() {
        String uid = prefs.getString("uid", null);
        if (uid == null) {
            uid = UUID.randomUUID().toString();
            prefs.edit().putString("uid", uid).apply();
        }
        return uid;
    }

    public String getFullStreamUrl() {
        String proto = getStreamProtocol().toUpperCase();
        if (proto.equals("RTMP") || proto.equals("RTMPS")) {
            String url = getStreamUrl().replaceAll("/$", "");
            String key = getStreamKey();
            return key.isEmpty() ? url : url + "/" + key;
        }
        return getStreamUrl();
    }
}
```

---

### 4.7 Module 5 — OpenTAKServerClient

Port using OkHttp:

```java
public class OpenTAKServerClient {
    private final OkHttpClient http;
    private final StreamSettings settings;

    public OpenTAKServerClient(StreamSettings s) {
        settings = s;
        http = new OkHttpClient.Builder()
            .callTimeout(10, TimeUnit.SECONDS)
            .addInterceptor(chain -> {
                Request req = chain.request().newBuilder()
                    .header("Authorization", "Bearer " + s.getOpenTakServerToken())
                    .build();
                return chain.proceed(req);
            })
            .build();
    }

    public void postStream(String url, String protocol, String codec,
                           double lat, double lon) { /* POST /api/video */ }

    public void deleteStream() { /* DELETE /api/video/{uid} */ }

    public void ping(Callback<String> cb) { /* GET /api/healthcheck → version string */ }
}
```

---

### 4.8 Module 6 — Main Fragment

Android equivalent of `MainWindow.xaml` + `MainWindow.xaml.cs`.

**Layout (`fragment_main.xml`):**
```xml
<LinearLayout orientation="vertical">

  <!-- Header -->
  <LinearLayout orientation="horizontal">
    <TextView id="appTitle"/>
    <View id="streamDot" background="@drawable/dot_red"/>
    <TextView id="streamStatus"/>
    <TextView id="bitrateText"/>
    <Button id="settingsButton"/>
  </LinearLayout>

  <!-- Content -->
  <LinearLayout orientation="horizontal" weight="1">

    <!-- Camera preview (large portion) -->
    <androidx.camera.view.PreviewView id="previewView"
      layout_weight="3"/>

    <!-- Side panel -->
    <ScrollView layout_weight="1">
      <LinearLayout orientation="vertical">
        <Spinner id="cameraSpinner"/>
        <Button id="refreshButton"/>
        <Switch id="screenShareSwitch"/>
        <TextView id="streamUrlText"/>
        <Button id="startButton"/>
        <Button id="stopButton"/>
        <View id="takDot"/>
        <TextView id="takStatus"/>
        <Button id="connectButton"/>
        <TextView id="latText"/>
        <TextView id="lonText"/>
        <TextView id="altText"/>
        <TextView id="cotCountText"/>
        <!-- Expandable manual location entry -->
        <EditText id="manualLat"/>
        <EditText id="manualLon"/>
        <Button id="applyLocationButton"/>
      </LinearLayout>
    </ScrollView>
  </LinearLayout>

  <!-- Status bar -->
  <LinearLayout orientation="horizontal">
    <TextView id="statusBar"/>
    <TextView id="clockText"/>
  </LinearLayout>

</LinearLayout>
```

**Fragment lifecycle:**
- `onViewCreated`: init services, populate camera spinner, start clock `Handler` runnable
- `onDestroyView`: stop all handlers, stop streaming, disconnect CoT, release camera preview

**Timers (using Handler self-scheduling runnables):**
```java
// Clock
Handler mainHandler = new Handler(Looper.getMainLooper());
Runnable clockRunnable = new Runnable() {
    public void run() {
        clockText.setText(new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date()));
        mainHandler.postDelayed(this, 1000);
    }
};
mainHandler.post(clockRunnable);

// CoT sender
Runnable cotRunnable = new Runnable() {
    public void run() {
        cotService.sendSa(lat, lon, alt, 0, 0);
        cotCountText.setText(++cotSent + " sent");
        mainHandler.postDelayed(this, settings.getCotIntervalSeconds() * 1000L);
    }
};
```

**Camera preview (CameraX):**
```java
ListenableFuture<ProcessCameraProvider> future =
    ProcessCameraProvider.getInstance(requireContext());
future.addListener(() -> {
    ProcessCameraProvider provider = future.get();
    Preview preview = new Preview.Builder().build();
    preview.setSurfaceProvider(previewView.getSurfaceProvider());
    CameraSelector selector = new CameraSelector.Builder()
        .requireLensFacing(CameraSelector.LENS_FACING_BACK).build();
    provider.bindToLifecycle(getViewLifecycleOwner(), selector, preview);
}, ContextCompat.getMainExecutor(requireContext()));
```

**Start Stream flow:**
```
tap Start
  → StreamingService.start(settings)
  → update UI: green dot, disable Start, enable Stop
  → if recording: show REC badge
  → start stream timer runnable
  → start CoT timer runnable
  → CotService.sendVideoAnnouncement(streamUrl, lat, lon)
  → OpenTAKServerClient.postStream(...)
```

**Stop Stream flow:**
```
tap Stop
  → StreamingService.stop()
  → update UI: red dot, enable Start, disable Stop, hide REC badge
  → stop stream timer runnable
  → stop CoT timer runnable
  → OpenTAKServerClient.deleteStream()
```

**Settings re-apply** (when returning from SettingsFragment):
```java
cotService.disconnect();
cotService = new CotService(settings);
streamingService = new StreamingService(settings);
otakClient = new OpenTAKServerClient(settings);
```

---

### 4.9 Module 7 — Settings Fragment

Five-section scrollable `Fragment` or `PreferenceFragmentCompat` mirroring the Windows settings dialog:

| Section | Controls |
|---|---|
| **Stream** | Protocol `Spinner`, URL `EditText`, Key `EditText`, Username/Password `EditText`, (FFmpeg path not needed — ffmpeg-kit is bundled) |
| **Video** | Codec `Spinner`, Resolution `Spinner`, FrameRate `Spinner`, Bitrate `SeekBar` + label, Recording `Switch`, Recording path picker |
| **Audio** | Device `Spinner`, Codec `Spinner`, Bitrate `Spinner`, Mute `Switch` |
| **TAK** | Callsign `EditText`, Team `Spinner`, Role `Spinner`, Host `EditText`, Port `EditText`, TLS `Switch`, Multicast `Switch`, CoT interval `EditText` |
| **OpenTAK** | URL `EditText`, Token `EditText`, Enable `Switch`, Test `Button` (async OkHttp ping) |

Save button writes all values to `StreamSettings` (SharedPreferences). Protocol spinner change → auto-suggest URL template.

---

### 4.10 Module 8 — Plugin Entry Point

```java
// VideoStreamingLifecycle.java
public class VideoStreamingLifecycle extends AbstractPlugin {
    private VideoStreamingDropDown dropDown;

    @Override
    public void onCreate(MapView mapView, Bundle extras) {
        super.onCreate(mapView, extras);
        dropDown = new VideoStreamingDropDown(mapView, pluginContext);
        dropDown.setRetain(true);
    }

    @Override
    public void onStart() {
        super.onStart();
        dropDown.open();
    }

    @Override
    public void onStop() {
        super.onStop();
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        dropDown.destroy();
    }
}
```

```java
// VideoStreamingDropDown.java
public class VideoStreamingDropDown extends DropDownReceiver
    implements DropDown.OnStateListener {

    private MainFragment mainFragment;

    public VideoStreamingDropDown(MapView mapView, Context pluginCtx) {
        super(mapView);
        mainFragment = new MainFragment(pluginCtx);
    }

    @Override
    public void onReceive(Context ctx, Intent intent) {
        showDropDown(mainFragment,
            HALF_WIDTH, FULL_HEIGHT,   // landscape
            FULL_WIDTH, HALF_HEIGHT,   // portrait
            false, this);
    }

    @Override public void onDropDownVisible(boolean visible) {}
    @Override public void onDropDownSizeChanged(double w, double h) {}
    @Override public void onDropDownClose() {}
    @Override protected void dispatchNotification(String notification) {}
}
```

---

### 4.11 AndroidManifest.xml Permissions

```xml
<uses-permission android:name="android.permission.CAMERA"/>
<uses-permission android:name="android.permission.RECORD_AUDIO"/>
<uses-permission android:name="android.permission.INTERNET"/>
<uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE"/>
<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE"
    android:maxSdkVersion="28"/>
<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE"
    android:maxSdkVersion="32"/>
<uses-permission android:name="android.permission.FOREGROUND_SERVICE"/>
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION"/>
```

Camera and audio permissions must be requested at runtime (Android 6+). Screen capture additionally requires a user-facing permission dialog via `MediaProjectionManager`.

---

### 4.12 Key Dependencies (`build.gradle`)

```groovy
dependencies {
    // ffmpeg-kit (replaces FFmpeg subprocess; 'full' variant includes all codecs)
    implementation 'com.arthenica:ffmpeg-kit-full:6.0-2'

    // CameraX (camera preview)
    implementation 'androidx.camera:camera-camera2:1.3.4'
    implementation 'androidx.camera:camera-lifecycle:1.3.4'
    implementation 'androidx.camera:camera-view:1.3.4'

    // OkHttp (OpenTAK REST API)
    implementation 'com.squareup.okhttp3:okhttp:4.12.0'

    // Gson or Moshi (JSON serialization for REST payloads)
    implementation 'com.google.code.gson:gson:2.10.1'

    // ATAK SDK (provided by the ATAK plugin build system)
    compileOnly fileTree(dir: 'libs', include: ['atak-sdk.jar'])
}
```

> **Note:** ffmpeg-kit-full adds ~30 MB to the APK. If binary size is a concern, use `ffmpeg-kit-https` (RTMP over HTTPS only) or `ffmpeg-kit-rtmp` variants and strip unused codecs.

---

### 4.13 Implementation Order for an Agent

Follow this sequence to build the plugin incrementally and validate each layer before building on top of it:

1. **Project skeleton** — ATAK plugin Gradle structure, `AndroidManifest.xml`, plugin lifecycle registration, empty `DropDownReceiver`
2. **`StreamSettings`** — SharedPreferences getters/setters + UID generation; write a unit test for `getFullStreamUrl()`
3. **`CotBuilder`** — pure XML generation with no Android dependencies; validate XML output against a running ATAK instance
4. **`CotService`** — TCP connect/disconnect, SA send, multicast send; test against a local TAK server (e.g., FreeTAKServer)
5. **`OpenTAKServerClient`** — OkHttp REST calls; test against an OpenTAK Server instance
6. **`StreamingService`** — ffmpeg-kit integration starting with RTMP + H.264 camera input; validate the stream in VLC before wiring to UI
7. **`SettingsFragment`** — wire all controls to `StreamSettings`; save/restore round-trip
8. **`MainFragment`** — camera preview, Start/Stop flow, all status indicators, location entry
9. **Wire everything in `VideoStreamingDropDown`** — connect services to fragment callbacks
10. **Screen share** — `MediaProjection` permission request + `VirtualDisplay` + ffmpeg-kit pipe
11. **Recording** — secondary ffmpeg-kit output (`-c copy -f mp4 <path>`)
12. **End-to-end test** — connect to TAK server, start stream, verify CoT SA and video events appear correctly in ATAK on another device

---

### 4.14 ATAK-Specific Enhancements Beyond WinTAK-ICU

The Android plugin can leverage native ATAK APIs that have no equivalent in the Windows app:

| Enhancement | ATAK API |
|---|---|
| Automatic GPS (no manual entry needed) | `MapView.getSelfMarker().getPoint()` or Android `LocationManager` |
| Route CoT through ATAK's pipeline | `CotDispatcher.getInstance().dispatch(CotEvent)` |
| Show streaming marker on ATAK map | `MapView.getRootGroup().addItem(new PointMapItem(...))` |
| Receive CoT from other ATAK clients | Subscribe to `CotEventDispatcher` |
| Use ATAK's certificate store for TLS | `AtakCertificate` / ATAK's `CertificateManager` |
| Open stream in ATAK's built-in video viewer | Fire `ConnectionEntry` intent handled by ATAK video manager |
| Plugin toolbar icon | Register in `plugin.xml` `<tool>` block |

---

## 5. Data Flow Diagrams

### 5.1 Streaming Start Flow

```
User taps Start Stream
    │
    ├─→ StreamingService.start()
    │       └─→ ffmpeg-kit: camera/screen → encode → RTMP/RTSP/SRT/UDP output
    │               (StatisticsCallback fires → BitrateUpdated)
    │
    ├─→ CotService.sendVideoAnnouncement(streamUrl, lat, lon)  [one-time]
    │       ├─→ TCP write to TAK Server  (b-i-v event)
    │       └─→ UDP multicast 239.2.3.1:6969
    │
    ├─→ OpenTAKServerClient.postStream(...)  [one-time]
    │       └─→ HTTP POST /api/video
    │
    └─→ Start CoT SA timer (every N seconds)
            └─→ CotService.sendSa(lat, lon, alt, speed, course)
                    ├─→ TCP write to TAK Server  (a-f-G-U-C event)
                    └─→ UDP multicast 239.2.3.1:6969
```

### 5.2 Streaming Stop Flow

```
User taps Stop Stream
    │
    ├─→ StreamingService.stop()  →  FFmpegKit.cancel()
    ├─→ Stop CoT timer
    ├─→ Update UI (red dot, enable Start, disable Stop)
    └─→ OpenTAKServerClient.deleteStream()
            └─→ HTTP DELETE /api/video/{uid}
```

### 5.3 Settings Save Flow

```
User saves settings
    │
    └─→ StreamSettings.save(all fields)  →  SharedPreferences.Editor.apply()
            │
            └─→ MainFragment.recreateServices()
                    ├─→ CotService.disconnect() → new CotService(settings)
                    ├─→ StreamingService.stop() → new StreamingService(settings)
                    └─→ new OpenTAKServerClient(settings)
```

---

## 6. Complete Feature-to-Implementation Mapping

| Feature | WinTAK-ICU | ATAK Plugin |
|---|---|---|
| RTMP streaming | FFmpeg `-f flv rtmp://...` | ffmpeg-kit `-f flv rtmp://...` |
| RTMPS streaming | FFmpeg `-f flv rtmps://...` | ffmpeg-kit `-f flv rtmps://...` |
| RTSP streaming | FFmpeg `-f rtsp rtsp://...` | ffmpeg-kit `-f rtsp rtsp://...` |
| SRT streaming | FFmpeg `-f mpegts srt://...` | ffmpeg-kit `-f mpegts srt://...` |
| UDP multicast streaming | FFmpeg `-f mpegts udp://239.2.3.1:6969` | ffmpeg-kit same |
| H.264 encoding | `libx264 -preset ultrafast -tune zerolatency` | Same via ffmpeg-kit |
| H.265 encoding | `libx265 -preset ultrafast -tune zerolatency` | Same via ffmpeg-kit |
| AV1 encoding | `libaom-av1 -cpu-used 8 -row-mt 1` | Same via ffmpeg-kit |
| AAC audio | `-acodec aac` | Same via ffmpeg-kit |
| OPUS audio | `-acodec libopus` | Same via ffmpeg-kit |
| G.711 audio | `-acodec pcm_mulaw` | Same via ffmpeg-kit |
| Camera preview | LibVLC DirectShow `VideoView` | CameraX `PreviewView` |
| Camera enumeration | FFmpeg dshow device list | `CameraManager.getCameraIdList()` |
| Screen capture | FFmpeg gdigrab | `MediaProjection` + `VirtualDisplay` |
| MP4 recording | FFmpeg `-c copy -f mp4 <file>` | ffmpeg-kit second output |
| SA CoT events | `CotBuilder.BuildSaEvent()` (C#) | `CotBuilder.buildSaEvent()` (Java) |
| Video CoT events | `CotBuilder.BuildVideoEvent()` (C#) | `CotBuilder.buildVideoEvent()` (Java) |
| TCP CoT delivery | `TcpClient` + `NetworkStream` | `java.net.Socket` |
| TLS CoT delivery | `SslStream` + permissive validator | `SSLSocket` + permissive `TrustManager` |
| UDP multicast CoT | `UdpClient.JoinMulticastGroup` | `MulticastSocket` + `WifiManager.MulticastLock` |
| OpenTAK Server API | `System.Net.Http.HttpClient` REST | OkHttp REST |
| Settings persistence | JSON file in `%AppData%` | `SharedPreferences` |
| Bitrate monitoring | FFmpeg stderr parse | ffmpeg-kit `StatisticsCallback` |
| Manual GPS entry | `TextBox` lat/lon + Apply button | `EditText` + Button |
| Auto GPS | Not implemented | Android `LocationManager` (enhancement) |
| TAK team color → CoT type | 12-color dictionary | Same mapping |
| CoT stale timing | 30 s SA / 1 h video | Same values |
| RTMP credential injection | URL construction `user:pass@host` | Same URL construction |
| Settings UI | 5-tab WPF `TabControl` | 5-section Android `Fragment` |
| Start/Stop controls | WPF `Button` pair | Android `Button` pair |
| Stream status indicator | Colored `Ellipse` dot | `ImageView` with drawable state |
| TAK connection indicator | Colored `Ellipse` dot | `ImageView` with drawable state |
| Elapsed stream timer | `DispatcherTimer` 1 s | `Handler.postDelayed` 1 s |
| CoT send counter | `int` + status label | `int` + `TextView` |
| OpenTAK stream registration | `POST /api/video` on start | Same via OkHttp |
| OpenTAK stream deregistration | `DELETE /api/video/{uid}` on stop | Same via OkHttp |

---

## 7. Open Questions

- Ask about the plane icon with FOV coming off it and sliders to adjust how big and where it comes off the plane.

---

*This document was generated from a full static analysis of the WinTAK-ICU source code in `jpat-12/wintak-icu` (commit `e177334`). All feature descriptions, data flows, and implementation mappings are derived directly from the source files.*
