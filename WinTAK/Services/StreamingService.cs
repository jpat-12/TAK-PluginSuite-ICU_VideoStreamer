using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICUVideoStreamer.Models;

namespace ICUVideoStreamer.Services
{
    public class StreamingService : IDisposable
    {
        private readonly AppSettings _settings;
        private Process _ffmpeg;
        private bool _disposed;

        public bool IsStreaming { get; private set; }
        public bool IsRecording => IsStreaming && _settings.EnableRecording;

        public event Action<int> BitrateUpdated;
        public event Action<string> StatusChanged;
        public event Action Stopped;

        private static readonly Regex BitrateRegex =
            new Regex(@"bitrate=\s*([\d.]+)kbits", RegexOptions.Compiled);

        private readonly System.Collections.Generic.Queue<string> _stderrLines =
            new System.Collections.Generic.Queue<string>(10);

        public StreamingService(AppSettings settings)
        {
            _settings = settings;
        }

        public void Start(string klvPipeName = null)
        {
            if (IsStreaming) return;

            if (!File.Exists(_settings.ResolvedFfmpegPath))
            {
                StatusChanged?.Invoke("FFmpeg not found at: " + _settings.ResolvedFfmpegPath);
                return;
            }

            lock (_stderrLines) _stderrLines.Clear();
            string args = BuildArguments(klvPipeName);

            // Write the full FFmpeg command to a temp file for diagnostics.
            // Check %TEMP%\ICUVideoStreamer_ffmpeg_cmd.txt if KLV is not appearing.
            try
            {
                string log = Path.Combine(Path.GetTempPath(), "ICUVideoStreamer_ffmpeg_cmd.txt");
                File.WriteAllText(log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n"
                    + _settings.ResolvedFfmpegPath + "\r\n"
                    + args + "\r\n");
            }
            catch { /* non-fatal */ }

            _ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _settings.ResolvedFfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            _ffmpeg.ErrorDataReceived += OnFfmpegStderr;
            _ffmpeg.Exited += OnFfmpegExited;

            try
            {
                _ffmpeg.Start();
                _ffmpeg.BeginErrorReadLine();
                IsStreaming = true;

                string p = (_settings.StreamProtocol ?? "").ToUpperInvariant();
                bool klvSkipped = klvPipeName != null &&
                                  (p == "RTSP" || p == "RTSPS" || p == "RTMP" || p == "RTMPS");
                StatusChanged?.Invoke(klvSkipped
                    ? "Streaming (no KLV on " + p + " — publish SRT to send KLV)"
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

            try
            {
                _ffmpeg.StandardInput.Write("q");
                _ffmpeg.StandardInput.Flush();
                bool exited = _ffmpeg.WaitForExit(3000);
                if (!exited) _ffmpeg.Kill();
            }
            catch { }

            _ffmpeg = null;
            StatusChanged?.Invoke("Stopped");
            Stopped?.Invoke();
        }

        private string BuildArguments(string klvPipeName = null)
        {
            var parts = new List<string>();
            string proto = (_settings.StreamProtocol ?? "RTMP").ToUpperInvariant();

            // KLV requires an MPEG-TS container, so it can only ride SRT / UDP
            // (-f mpegts carries the data PID natively). Direct RTSP publish can
            // NOT carry KLV from FFmpeg: its RTP muxer has no KLV payloader
            // ("Unsupported codec klv"), and -f rtp_mpegts doesn't speak rtsp://
            // at all ("Protocol not found") — both verified against ffmpeg 8.1.1.
            //
            // The working KLV path (verified against MediaMTX v1.19): publish SRT,
            // and MediaMTX re-serves the stream over RTSP with the KLV as a proper
            // SMPTE336M RTP track that TAK clients (Gv2F player) understand.
            bool klvEnabled = klvPipeName != null &&
                              (proto == "SRT" || proto == "UDP");

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

            // Input 1: KLV metadata pipe (MPEG-TS only).
            //
            // KlvService writes a minimal MPEG-TS stream to the pipe: PAT + PMT
            // declaring stream_type=0x06 with a 'KLVA' registration descriptor,
            // followed by PES-wrapped MISB ST 0601 KLV data.
            //
            // FFmpeg reads it as -f mpegts, recognises the KLVA registration
            // descriptor (see mpegts.c), sets codec_id = AV_CODEC_ID_SMPTE_KLV,
            // and the output mpegts muxer then relays it at stream_type 0x06
            // (STANAG 4609 / SMPTE 336M) in the destination TS.
            //
            // -use_wallclock_as_timestamps 1 gives the data stream wall-clock PTS
            // so the mpegts muxer can interleave it without PCR in the sub-stream.
            if (klvEnabled)
            {
                string pipePath = @"\\.\pipe\" + klvPipeName;
                // -analyzeduration 500000 = 0.5 s probe; keeps stream startup fast.
                // Default is 5 s which would stall the camera input as well.
                parts.Add(string.Format(
                    "-use_wallclock_as_timestamps 1 -analyzeduration 500000 -probesize 8192 -f mpegts -i \"{0}\"",
                    pipePath));
            }

            // Video codec
            switch ((_settings.VideoCodec ?? "H264").ToUpperInvariant())
            {
                case "H265":
                    parts.Add("-vcodec libx265 -preset ultrafast -tune zerolatency");
                    break;
                case "AV1":
                    parts.Add("-vcodec libaom-av1 -cpu-used 8 -row-mt 1");
                    break;
                default: // H264
                    parts.Add("-vcodec libx264 -preset ultrafast -tune zerolatency");
                    break;
            }

            // Resolution, framerate, bitrate
            string res = IsValidResolution(_settings.Resolution) ? _settings.Resolution : "1280x720";
            int fps = _settings.FrameRate > 0 && _settings.FrameRate <= 120 ? _settings.FrameRate : 30;
            int bv  = _settings.VideoBitrate > 0 && _settings.VideoBitrate <= 100_000 ? _settings.VideoBitrate : 2000;
            int maxrate = (int)(bv * 1.5);
            int bufsize = bv * 2;

            parts.Add(string.Format("-vf scale={0}", res.Replace("x", ":")));
            parts.Add(string.Format("-r {0}", fps));
            parts.Add(string.Format("-b:v {0}k -maxrate {1}k -bufsize {2}k", bv, maxrate, bufsize));

            // Audio — suppress if muted or no audio device is configured
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
                    case "OPUS":
                        parts.Add(string.Format("-acodec libopus -b:a {0}k", ab));
                        break;
                    case "G711":
                        parts.Add(string.Format("-acodec pcm_mulaw -b:a {0}k", ab));
                        break;
                    default: // AAC
                        parts.Add(string.Format("-acodec aac -b:a {0}k", ab));
                        break;
                }
            }

            // Primary output — sanitize URL to prevent argument injection
            string streamUrl = SanitizeArg(_settings.FullStreamUrl);
            switch (proto)
            {
                case "RTSP":
                case "RTSPS":
                    // Standard RTSP: per-codec RTP streams. FFmpeg cannot payload KLV
                    // over RTP (see klvEnabled above) — video/audio only on this path.
                    // Operators who need KLV should publish SRT to MediaMTX, which
                    // re-serves RTSP with an SMPTE336M KLV track for TAK clients.
                    // Force TCP transport — UDP fails through NAT/firewall.
                    parts.Add(string.Format("-f rtsp -rtsp_transport tcp \"{0}\"", streamUrl));
                    break;
                case "SRT":
                    if (klvEnabled) parts.Add("-map 0 -map 1:d -c:d copy");
                    parts.Add(string.Format("-f mpegts \"{0}\"", streamUrl));
                    break;
                case "UDP":
                    if (klvEnabled) parts.Add("-map 0 -map 1:d -c:d copy");
                    parts.Add("-f mpegts \"udp://239.2.3.1:6969\"");
                    break;
                default: // RTMP / RTMPS
                    parts.Add(string.Format("-f flv \"{0}\"", streamUrl));
                    break;
            }

            // Optional recording — always maps only camera input, never KLV
            if (_settings.EnableRecording && !string.IsNullOrEmpty(_settings.RecordingPath))
            {
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string recPath = SanitizeArg(Path.Combine(_settings.RecordingPath, ts + ".mp4"));
                if (klvEnabled) parts.Add("-map 0");
                parts.Add(string.Format("-c copy -f mp4 \"{0}\"", recPath));
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

        private static bool IsValidResolution(string s) =>
            s != null && Regex.IsMatch(s, @"^\d{2,5}x\d{2,5}$");

        private void OnFfmpegStderr(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // Keep a rolling window of the last 5 lines for error reporting
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

            string lastOutput;
            lock (_stderrLines)
                lastOutput = _stderrLines.Count > 0
                    ? string.Join(" | ", _stderrLines)
                    : "no output captured";

            StatusChanged?.Invoke("FFmpeg stopped: " + lastOutput);
            Stopped?.Invoke();
        }

        public static List<string> EnumerateVideoDevices(string ffmpegPath)
        {
            var devices = EnumerateVideoDevicesNative();
            if (devices.Count > 0) return devices;

            // Fallback: ask FFmpeg to list DirectShow devices
            try
            {
                string exe = (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                    ? ffmpegPath
                    : AppSettings.BundledFfmpegPath;
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "-list_devices true -f dshow -i dummy",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
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
                var devEnumObj = Activator.CreateInstance(devEnumType);
                var devEnum = devEnumObj as ICreateDevEnum;
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
            int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                      [In] ref object pVar);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
