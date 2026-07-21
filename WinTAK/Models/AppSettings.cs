using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ICUVideoStreamer.Models
{
    [DataContract]
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ICU-VideoStreamer", "settings.json");

        // Stream
        [DataMember] public string StreamProtocol { get; set; } = "RTMP";
        [DataMember] public string StreamUrl      { get; set; } = "rtmp://localhost/live";
        [DataMember] public string StreamKey      { get; set; } = "stream";
        [DataMember] public string StreamUsername { get; set; } = "";
        [DataMember] public bool   UseTls         { get; set; } = false;
        [DataMember] public string CertificatePath{ get; set; } = "";

        // Credentials — stored DPAPI-encrypted; runtime prop is NOT serialized
        [DataMember] public string StreamPasswordProtected { get; set; } = "";

        public string StreamPassword { get; set; } = "";

        // Video
        [DataMember] public string VideoDevice       { get; set; } = "";
        [DataMember] public string VideoCodec        { get; set; } = "H264";
        [DataMember] public int    VideoBitrate      { get; set; } = 2000;
        [DataMember] public string Resolution        { get; set; } = "1280x720";
        [DataMember] public int    FrameRate         { get; set; } = 30;
        [DataMember] public bool   EnableRecording   { get; set; } = false;
        [DataMember] public string RecordingPath     { get; set; } = "";
        [DataMember] public bool   EnableScreenShare { get; set; } = false;

        // Audio
        [DataMember] public string AudioDevice  { get; set; } = "";
        [DataMember] public string AudioCodec   { get; set; } = "AAC";
        [DataMember] public int    AudioBitrate { get; set; } = 128;
        [DataMember] public bool   MuteAudio    { get; set; } = false;

        // KLV / Sensor Metadata — matches MISB ST 0601 fields sent by TAK ICU
        [DataMember] public string SensorName { get; set; } = "WinTAK-ICU";
        [DataMember] public double SensorHFov { get; set; } = 60.0;  // Tag 16, degrees
        [DataMember] public double SensorVFov { get; set; } = 34.0;  // Tag 17, degrees

        // Identity — UID persisted so it's stable across restarts; callsign comes from WinTAK at runtime
        [DataMember] public string Uid { get; set; } = "";

        // Map-anchored status widget (the floating plugin-icon HUD). Position is stored
        // as an offset from the WinTAK main window's top-left so it tracks the window.
        // WidgetPositioned=false means "auto-place at bottom-left" until the user drags it.
        [DataMember] public bool   WidgetPositioned { get; set; } = false;
        [DataMember] public double WidgetOffsetX    { get; set; } = 0;
        [DataMember] public double WidgetOffsetY    { get; set; } = 0;

        // FFmpeg — empty means use the bundled binary resolved at runtime
        [DataMember] public string FfmpegPath { get; set; } = "";

        // Bundled ffmpeg.exe lives next to the plugin DLL in a ffmpeg\ subfolder.
        public static readonly string BundledFfmpegPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            "ffmpeg", "ffmpeg.exe");

        public string ResolvedFfmpegPath =>
            !string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath)
                ? FfmpegPath
                : BundledFfmpegPath;

        // ── Derived URLs ─────────────────────────────────────────────────────────

        /// <summary>Full URL passed to FFmpeg — includes injected credentials.</summary>
        public string FullStreamUrl
        {
            get
            {
                string proto = (StreamProtocol ?? "").ToUpperInvariant();

                // Build base URL + stream key for all protocols
                string baseUrl = (StreamUrl ?? "").TrimEnd('/');
                string streamKey = StreamKey ?? "";
                string fullUrl = string.IsNullOrEmpty(streamKey)
                    ? baseUrl
                    : baseUrl + "/" + streamKey;

                // Inject credentials using the correct format per protocol:
                // RTSP uses standard userinfo in the URL (rtsp://user:pass@host/path)
                // RTMP/SRT/others use MediaMTX query-string format (?user=X&pass=Y)
                if (!string.IsNullOrEmpty(StreamUsername) && !string.IsNullOrEmpty(StreamPassword))
                {
                    string p = proto;
                    if (p == "RTSP" || p == "RTSPS")
                    {
                        // FFmpeg publishes via RTSP ANNOUNCE + digest auth challenge-response.
                        // Credentials must be in the URL userinfo so FFmpeg can answer the 401.
                        int schemeEnd = fullUrl.IndexOf("://");
                        if (schemeEnd >= 0)
                        {
                            string userInfo = Uri.EscapeDataString(StreamUsername) + ":"
                                           + Uri.EscapeDataString(StreamPassword) + "@";
                            fullUrl = fullUrl.Substring(0, schemeEnd + 3) + userInfo
                                    + fullUrl.Substring(schemeEnd + 3);
                        }
                    }
                    else
                    {
                        // RTMP/SRT/others: MediaMTX query-string format (?user=X&pass=Y)
                        string sep = fullUrl.Contains("?") ? "&" : "?";
                        fullUrl += sep
                                 + "user=" + Uri.EscapeDataString(StreamUsername)
                                 + "&pass=" + Uri.EscapeDataString(StreamPassword);
                    }
                }

                return fullUrl;
            }
        }

        /// <summary>Display-safe URL — password replaced with asterisks.</summary>
        public string DisplayStreamUrl
        {
            get
            {
                if (string.IsNullOrEmpty(StreamPassword)) return FullStreamUrl;
                return FullStreamUrl.Replace(
                    Uri.EscapeDataString(StreamPassword), "***");
            }
        }

        /// <summary>Public stream URL without embedded credentials — safe to broadcast in CoT and OpenTAK registration.</summary>
        public string PublicStreamUrl
        {
            get
            {
                string url = (StreamUrl ?? "").TrimEnd('/');
                string key = StreamKey ?? "";
                return string.IsNullOrEmpty(key) ? url : url + "/" + key;
            }
        }

        // ── Persistence ──────────────────────────────────────────────────────────

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    using (var fs = File.OpenRead(SettingsPath))
                    {
                        var s = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings))
                                    .ReadObject(fs);
                        if (string.IsNullOrEmpty(s.Uid))
                            s.Uid = Guid.NewGuid().ToString();

                        // DataContractJsonSerializer zeroes missing fields instead of
                        // using declared initialiser values.  Patch fields that were
                        // added after the initial release so old settings files still
                        // get sensible defaults on first load after an upgrade.
                        if (string.IsNullOrEmpty(s.SensorName)) s.SensorName = "WinTAK-ICU";
                        if (s.SensorHFov   <= 0) s.SensorHFov   = 60.0;
                        if (s.SensorVFov   <= 0) s.SensorVFov   = 34.0;
                        if (s.FrameRate    <= 0) s.FrameRate     = 30;
                        if (s.VideoBitrate <= 0) s.VideoBitrate  = 2000;
                        if (s.AudioBitrate <= 0) s.AudioBitrate  = 128;

                        // Decrypt credentials into runtime-only property
                        s.StreamPassword = Unprotect(s.StreamPasswordProtected);
                        return s;
                    }
                }
            }
            catch { }

            var defaults = new AppSettings { Uid = Guid.NewGuid().ToString() };
            defaults.Save();
            return defaults;
        }

        public void Save()
        {
            try
            {
                // Encrypt credentials before persisting
                StreamPasswordProtected = Protect(StreamPassword);

                string dir = Path.GetDirectoryName(SettingsPath);
                Directory.CreateDirectory(dir);
                RestrictDirectoryToCurrentUser(dir);

                using (var fs = File.Create(SettingsPath))
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(fs, this);
            }
            catch { }
        }

        // ── DPAPI helpers ─────────────────────────────────────────────────────────

        private static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try
            {
                byte[] encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext),
                    null,
                    DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch { return ""; }
        }

        private static string Unprotect(string ciphertext)
        {
            if (string.IsNullOrEmpty(ciphertext)) return "";
            try
            {
                byte[] decrypted = ProtectedData.Unprotect(
                    Convert.FromBase64String(ciphertext),
                    null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch { return ""; }
        }

        // ── File ACL ──────────────────────────────────────────────────────────────

        private static void RestrictDirectoryToCurrentUser(string path)
        {
            try
            {
                var di   = new System.IO.DirectoryInfo(path);
                var acl  = di.GetAccessControl();
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;

                // Remove inherited rules and grant current user full control only
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                di.SetAccessControl(acl);
            }
            catch { /* non-fatal — ACL hardening is best-effort */ }
        }
    }
}
