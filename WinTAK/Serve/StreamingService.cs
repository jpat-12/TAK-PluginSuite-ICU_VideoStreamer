using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICUVideoStreamer.Capture;
using ICUVideoStreamer.Models;

namespace ICUVideoStreamer.Serve
{
    /// <summary>
    /// FFmpeg subprocess manager. Mirrors the ATAK plugin's <c>serve/</c> transports:
    /// it publishes the encoded stream to the destination selected in
    /// <see cref="MediaServerConfig"/> — UDP multicast for LAN, or a push to a media
    /// server (RTMP/RTSP/SRT). KLV metadata (from the pipe) rides only the MPEG-TS
    /// paths (LAN/UDP and SRT), matching the verified transport constraints.
    /// </summary>
    public class StreamingService : IDisposable
    {
        // Live preview (teed off the streaming FFmpeg so the camera is opened once).
        private const int PreviewWidth  = 640;
        private const int PreviewHeight = 360;

        private readonly AppSettings _settings;
        private Process _ffmpeg;
        private PreviewRenderer _preview;
        private bool _disposed;

        public bool IsStreaming { get; private set; }

        /// <summary>Live preview bitmap teed off the encode (null when preview is disabled).</summary>
        public WriteableBitmap PreviewBitmap => _preview?.Bitmap;

        public event Action<int>    BitrateUpdated;
        public event Action<string> StatusChanged;
        public event Action         Stopped;
        /// <summary>Raised once the first live preview frame has rendered.</summary>
        public event Action         PreviewFrameReady;

        private static readonly Regex BitrateRegex =
            new Regex(@"bitrate=\s*([\d.]+)kbits", RegexOptions.Compiled);

        private readonly Queue<string> _stderrLines = new Queue<string>(10);
        private volatile bool _stopRequested; // true when the user asked to stop (vs. an unexpected exit)

        public StreamingService(AppSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Start publishing. <paramref name="server"/> and <paramref name="encoder"/> are
        /// snapshots taken at broadcast time; <paramref name="klvPipeName"/> is the KLV
        /// named-pipe (or null to stream without metadata).
        /// </summary>
        public void Start(MediaServerConfig server, EncoderConfig encoder, string klvPipeName = null,
                          Dispatcher previewDispatcher = null)
        {
            if (IsStreaming) return;

            if (!File.Exists(_settings.ResolvedFfmpegPath))
            {
                StatusChanged?.Invoke("FFmpeg not found at: " + _settings.ResolvedFfmpegPath);
                return;
            }

            bool withPreview = previewDispatcher != null;
            _stopRequested = false;

            lock (_stderrLines) _stderrLines.Clear();
            string args = BuildArguments(server, encoder, klvPipeName, withPreview);

            // Full command dumped to %TEMP% for diagnostics (check if KLV is missing).
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "ICUVideoStreamer_ffmpeg_cmd.txt");
                File.WriteAllText(log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n"
                    + _settings.ResolvedFfmpegPath + "\r\n" + args + "\r\n");
            }
            catch { /* non-fatal */ }

            _ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = _settings.ResolvedFfmpegPath,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = withPreview,
                    CreateNoWindow         = true,
                },
                EnableRaisingEvents = true,
            };

            _ffmpeg.ErrorDataReceived += OnFfmpegStderr;
            _ffmpeg.Exited            += OnFfmpegExited;

            try
            {
                _ffmpeg.Start();
                _ffmpeg.BeginErrorReadLine();
                IsStreaming = true;

                if (withPreview)
                {
                    _preview = new PreviewRenderer(PreviewWidth, PreviewHeight);
                    _preview.FirstFrame += () => PreviewFrameReady?.Invoke();
                    _preview.Start(_ffmpeg.StandardOutput.BaseStream, previewDispatcher);
                }

                bool klvSkipped = klvPipeName != null && !server.KlvCapable;
                StatusChanged?.Invoke(klvSkipped
                    ? "Streaming (no KLV on " + server.ProtocolName + " — use SRT or LAN for KLV)"
                    : "Streaming");
            }
            catch (Exception ex)
            {
                IsStreaming = false;
                StatusChanged?.Invoke("Failed to start FFmpeg: " + ex.Message);
                _ffmpeg = null;
            }
        }

        public void Stop()
        {
            if (_ffmpeg == null || !IsStreaming) return;
            IsStreaming = false;
            _stopRequested = true; // suppress the "unexpected exit" status from OnFfmpegExited

            try { _preview?.Stop(); } catch { }
            _preview = null;

            try
            {
                _ffmpeg.StandardInput.Write("q");
                _ffmpeg.StandardInput.Flush();
                if (!_ffmpeg.WaitForExit(3000)) _ffmpeg.Kill();
            }
            catch { }

            _ffmpeg = null;
            StatusChanged?.Invoke("Stopped");
            Stopped?.Invoke();
        }

        // ── FFmpeg argument construction ───────────────────────────────────────────

        private string BuildArguments(MediaServerConfig server, EncoderConfig encoder,
                                      string klvPipeName, bool withPreview)
        {
            var parts = new List<string>();

            // KLV requires an MPEG-TS container, so it can only ride SRT / UDP(LAN).
            // Direct RTSP/RTMP publish cannot carry KLV from FFmpeg (RTP has no KLV
            // payloader) — verified against ffmpeg 8.1.1 + MediaMTX v1.19.
            bool klvEnabled = klvPipeName != null && server.KlvCapable;

            // Input 0: camera or screen
            if (_settings.EnableScreenShare)
            {
                parts.Add("-f gdigrab -framerate 30 -i desktop");
            }
            else
            {
                string videoDevice = SanitizeArg(_settings.VideoDevice);
                string audioDevice = SanitizeArg(_settings.AudioDevice);
                string input = "video=" + videoDevice;
                if (!string.IsNullOrEmpty(audioDevice) && !_settings.MuteAudio)
                    input += ":audio=" + audioDevice;
                parts.Add("-f dshow -i \"" + input + "\"");
            }

            // Input 1: KLV metadata pipe (MPEG-TS). KlvService writes a minimal TS
            // (PAT + PMT with a KLVA registration descriptor + PES-wrapped MISB KLV);
            // FFmpeg reads it as SMPTE_KLV and the output muxer relays stream_type 0x06.
            if (klvEnabled)
            {
                string pipePath = @"\\.\pipe\" + klvPipeName;
                parts.Add(string.Format(
                    "-use_wallclock_as_timestamps 1 -analyzeduration 500000 -probesize 8192 -f mpegts -i \"{0}\"",
                    pipePath));
            }

            // Video codec
            switch ((_settings.VideoCodec ?? "H264").ToUpperInvariant())
            {
                case "H265": parts.Add("-vcodec libx265 -preset ultrafast -tune zerolatency"); break;
                case "AV1":  parts.Add("-vcodec libaom-av1 -cpu-used 8 -row-mt 1");            break;
                default:     parts.Add("-vcodec libx264 -preset ultrafast -tune zerolatency"); break;
            }

            // Video filter: optional rotation, then scale to the selected resolution.
            string rotation = encoder.RotationFilter();
            string scale    = "scale=" + EncoderConfig.ScaleArg(encoder.resolution);
            parts.Add("-vf \"" + (rotation != null ? rotation + "," + scale : scale) + "\"");

            int fps = encoder.fps > 0 && encoder.fps <= 120 ? encoder.fps : 30;
            int bv  = encoder.bitrateKbps > 0 && encoder.bitrateKbps <= 100_000 ? encoder.bitrateKbps : 2000;
            parts.Add(string.Format("-r {0}", fps));
            parts.Add(string.Format("-b:v {0}k -maxrate {1}k -bufsize {2}k", bv, (int)(bv * 1.5), bv * 2));

            // Audio — suppress if muted or no audio device
            bool hasAudio = !string.IsNullOrWhiteSpace(_settings.AudioDevice) || _settings.EnableScreenShare;
            if (_settings.MuteAudio || !hasAudio)
            {
                parts.Add("-an");
            }
            else
            {
                int ab = _settings.AudioBitrate > 0 && _settings.AudioBitrate <= 2000 ? _settings.AudioBitrate : 128;
                switch ((_settings.AudioCodec ?? "AAC").ToUpperInvariant())
                {
                    case "OPUS": parts.Add(string.Format("-acodec libopus -b:a {0}k", ab));    break;
                    case "G711": parts.Add(string.Format("-acodec pcm_mulaw -b:a {0}k", ab));   break;
                    default:     parts.Add(string.Format("-acodec aac -b:a {0}k", ab));         break;
                }
            }

            // Primary output — destination-driven.
            string outUrl = SanitizeArg(server.PushUrlWithCredentials());
            if (server.destination == MediaServerConfig.Destination.LAN)
            {
                // LAN: MPEG-TS UDP multicast — carries KLV natively.
                if (klvEnabled) parts.Add("-map 0 -map 1:d -c:d copy");
                parts.Add(string.Format("-f mpegts \"{0}\"", outUrl));
            }
            else
            {
                switch (server.pushProtocol)
                {
                    case MediaServerConfig.PushProtocol.RTSP:
                        // Force TCP transport — UDP fails through NAT/firewall.
                        parts.Add(string.Format("-f rtsp -rtsp_transport tcp \"{0}\"", outUrl));
                        break;
                    case MediaServerConfig.PushProtocol.SRT:
                        if (klvEnabled) parts.Add("-map 0 -map 1:d -c:d copy");
                        parts.Add(string.Format("-f mpegts \"{0}\"", outUrl));
                        break;
                    default: // RTMP
                        parts.Add(string.Format("-f flv \"{0}\"", outUrl));
                        break;
                }
            }

            // Optional recording — always maps only the camera input, never KLV.
            if (_settings.EnableRecording && !string.IsNullOrEmpty(_settings.RecordingPath))
            {
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string recPath = SanitizeArg(Path.Combine(_settings.RecordingPath, ts + ".mp4"));
                if (klvEnabled) parts.Add("-map 0");
                parts.Add(string.Format("-c copy -f mp4 \"{0}\"", recPath));
            }

            // Live preview output: a small, low-rate raw bgr0 frame stream teed back to the
            // plugin over stdout so the operator keeps seeing the camera while broadcasting.
            // (No rotation here — the HUD applies display rotation, matching the idle preview.)
            if (withPreview)
            {
                parts.Add(string.Format(
                    "-map 0:v -an -r 15 -vf scale={0}:{1} -pix_fmt bgr0 -c:v rawvideo -f rawvideo pipe:1",
                    PreviewWidth, PreviewHeight));
            }

            parts.Add("-stats -loglevel warning");
            return string.Join(" ", parts);
        }

        // Strips characters that can escape quoted FFmpeg arguments on Windows.
        private static string SanitizeArg(string s)
        {
            if (s == null) return "";
            return s.Replace("\"", "").Replace("\r", "").Replace("\n", "").Replace("\0", "");
        }

        // ── FFmpeg stderr / lifecycle ──────────────────────────────────────────────

        private void OnFfmpegStderr(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            lock (_stderrLines)
            {
                if (_stderrLines.Count >= 5) _stderrLines.Dequeue();
                _stderrLines.Enqueue(e.Data.Trim());
            }

            var match = BitrateRegex.Match(e.Data);
            if (match.Success && double.TryParse(match.Groups[1].Value, out double kbps))
                BitrateUpdated?.Invoke((int)kbps);
        }

        private void OnFfmpegExited(object sender, EventArgs e)
        {
            IsStreaming = false;
            try { _preview?.Stop(); } catch { }
            _preview = null;

            // Intentional stop: Stop() already reported a clean status — don't clobber it
            // with an FFmpeg stderr dump.
            if (_stopRequested)
            {
                Stopped?.Invoke();
                return;
            }

            // Unexpected exit (crash / server refused / camera busy): surface a concise reason,
            // skipping FFmpeg's periodic "frame=… fps=…" stats lines which carry no error info.
            string reason = LastMeaningfulStderr();
            StatusChanged?.Invoke(string.IsNullOrEmpty(reason) ? "Stream ended unexpectedly" : "Stream stopped: " + reason);
            Stopped?.Invoke();
        }

        /// <summary>The most recent stderr line that isn't a periodic progress/stats line.</summary>
        private string LastMeaningfulStderr()
        {
            lock (_stderrLines)
            {
                foreach (var line in System.Linq.Enumerable.Reverse(_stderrLines))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("frame=") || (line.Contains("fps=") && line.Contains("bitrate="))) continue;
                    return line.Length > 160 ? line.Substring(0, 160) : line;
                }
            }
            return "";
        }

        // ── DirectShow device enumeration ──────────────────────────────────────────

        public static List<string> EnumerateVideoDevices(string ffmpegPath)
        {
            var devices = EnumerateVideoDevicesNative();
            if (devices.Count > 0) return devices;

            try
            {
                string exe = (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                    ? ffmpegPath
                    : AppSettings.BundledFfmpegPath;
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName              = exe,
                        Arguments             = "-list_devices true -f dshow -i dummy",
                        UseShellExecute       = false,
                        RedirectStandardError = true,
                        CreateNoWindow        = true,
                    }
                };
                p.Start();
                string output = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);

                bool inVideoSection = false;
                foreach (string line in output.Split('\n'))
                {
                    if (line.Contains("DirectShow video devices")) { inVideoSection = true; continue; }
                    if (line.Contains("DirectShow audio devices")) break;
                    if (inVideoSection)
                    {
                        var m = Regex.Match(line, "\"([^\"]+)\"");
                        if (m.Success) devices.Add(m.Groups[1].Value);
                    }
                }
            }
            catch { }
            return devices;
        }

        // DirectShow COM interop — enumerates video capture devices without FFmpeg.
        private static List<string> EnumerateVideoDevicesNative()
        {
            var devices = new List<string>();
            try
            {
                var devEnumType = Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86"));
                var devEnumObj  = Activator.CreateInstance(devEnumType);
                var devEnum     = devEnumObj as ICreateDevEnum;
                if (devEnum == null) return devices;

                var videoCat = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
                devEnum.CreateClassEnumerator(ref videoCat, out IEnumMoniker enumMoniker, 0);
                if (enumMoniker == null) return devices;

                var monikers = new IMoniker[1];
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0 && monikers[0] != null)
                {
                    object bagObj = null;
                    var bagGuid = typeof(IPropertyBag).GUID;
                    monikers[0].BindToStorage(null, null, ref bagGuid, out bagObj);
                    var bag = bagObj as IPropertyBag;
                    if (bag != null)
                    {
                        object val = "";
                        bag.Read("FriendlyName", ref val, IntPtr.Zero);
                        if (val is string name && !string.IsNullOrEmpty(name))
                            devices.Add(name);
                        Marshal.ReleaseComObject(bag);
                    }
                    Marshal.ReleaseComObject(monikers[0]);
                }
                Marshal.ReleaseComObject(enumMoniker);
                Marshal.ReleaseComObject(devEnumObj);
            }
            catch { }
            return devices;
        }

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator([In] ref Guid pType, [Out] out IEnumMoniker ppEnumMoniker, [In] int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                     [In, Out] ref object pVar, IntPtr pErrorLog);
            [PreserveSig]
            int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In] ref object pVar);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
