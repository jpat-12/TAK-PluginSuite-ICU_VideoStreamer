using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using ICUVideoStreamer.Capture;
using ICUVideoStreamer.Serve;

namespace ICUVideoStreamer.Models
{
    /// <summary>
    /// Persisted plugin settings. Mirrors the ATAK plugin's <c>util/Prefs</c> role:
    /// it stores the <see cref="MediaServerConfig"/> (destination) and
    /// <see cref="EncoderConfig"/> (capture) plus the WinTAK-specific extras (FFmpeg
    /// path, codec, audio, recording, KLV sensor metadata). JSON at
    /// <c>%AppData%\ICU-VideoStreamer\settings.json</c>; the server password is
    /// DPAPI-encrypted at rest.
    /// </summary>
    [DataContract]
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ICU-VideoStreamer", "settings.json");

        // ── Destination (MediaServerConfig) ────────────────────────────────────────
        [DataMember] public string Alias        { get; set; } = "VIDEO_1";
        [DataMember] public string Destination  { get; set; } = "SERVER"; // LAN | SERVER
        [DataMember] public string PushProtocol { get; set; } = "RTSP";   // RTMP | RTSP | SRT
        [DataMember] public string ServerHost   { get; set; } = "";
        [DataMember] public int    ServerPort   { get; set; } = 8554;
        [DataMember] public string StreamPath   { get; set; } = "icu";
        [DataMember] public string ServerUsername { get; set; } = "";

        // Server password — stored DPAPI-encrypted; runtime prop is NOT serialized.
        [DataMember] public string ServerPasswordProtected { get; set; } = "";
        public string ServerPassword { get; set; } = "";

        // ── Capture (EncoderConfig) ────────────────────────────────────────────────
        [DataMember] public string Resolution      { get; set; } = "P720"; // P480 | P720 | P1080
        [DataMember] public int    VideoBitrate    { get; set; } = 2000;
        [DataMember] public int    FrameRate       { get; set; } = 30;
        [DataMember] public int    RotationDegrees { get; set; } = 0;

        // ── Source ─────────────────────────────────────────────────────────────────
        [DataMember] public string VideoDevice       { get; set; } = "";
        [DataMember] public bool   EnableScreenShare { get; set; } = false;

        // ── Codec / audio / recording ──────────────────────────────────────────────
        [DataMember] public string VideoCodec      { get; set; } = "H264"; // H264 | H265 | AV1
        [DataMember] public string AudioDevice     { get; set; } = "";
        [DataMember] public string AudioCodec      { get; set; } = "AAC";  // AAC | OPUS | G711
        [DataMember] public int    AudioBitrate    { get; set; } = 128;
        [DataMember] public bool   MuteAudio       { get; set; } = false;
        [DataMember] public bool   EnableRecording { get; set; } = false;
        [DataMember] public string RecordingPath   { get; set; } = "";

        // ── KLV / sensor metadata (MISB ST 0601) ───────────────────────────────────
        [DataMember] public string SensorName { get; set; } = "WinTAK-ICU";
        [DataMember] public double SensorHFov { get; set; } = 60.0;
        [DataMember] public double SensorVFov { get; set; } = 34.0;

        // ── Identity / tooling ─────────────────────────────────────────────────────
        [DataMember] public string Uid        { get; set; } = "";
        [DataMember] public string FfmpegPath { get; set; } = "";

        // ── FFmpeg resolution ──────────────────────────────────────────────────────
        private static readonly string AsmDir =
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        // Preferred bundled location (ffmpeg\ffmpeg.exe next to the plugin DLL).
        public static readonly string BundledFfmpegPath = Path.Combine(AsmDir, "ffmpeg", "ffmpeg.exe");
        // Fallback if the package extractor flattened the ffmpeg folder.
        private static readonly string BundledFfmpegFlat = Path.Combine(AsmDir, "ffmpeg.exe");

        public string ResolvedFfmpegPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath)) return FfmpegPath;
                if (File.Exists(BundledFfmpegPath)) return BundledFfmpegPath;
                if (File.Exists(BundledFfmpegFlat)) return BundledFfmpegFlat;
                return BundledFfmpegPath; // default target (the settings dialog warns if missing)
            }
        }

        // ── MediaServerConfig <-> settings ─────────────────────────────────────────

        public MediaServerConfig ToServerConfig()
        {
            var c = new MediaServerConfig
            {
                alias      = Alias,
                host       = ServerHost ?? "",
                streamPath = string.IsNullOrEmpty(StreamPath) ? "icu" : StreamPath,
                username   = ServerUsername ?? "",
                password   = ServerPassword ?? "",
                serverPort = ServerPort > 0 ? ServerPort : 8554,
            };
            c.destination = ParseEnum(Destination, MediaServerConfig.Destination.SERVER);
            c.pushProtocol = ParseEnum(PushProtocol, MediaServerConfig.PushProtocol.RTSP);
            return c;
        }

        public void ApplyServerConfig(MediaServerConfig c)
        {
            Alias          = c.alias;
            Destination    = c.destination.ToString();
            PushProtocol   = c.pushProtocol.ToString();
            ServerHost     = c.host ?? "";
            ServerPort     = c.serverPort;
            StreamPath     = c.streamPath ?? "icu";
            ServerUsername = c.username ?? "";
            ServerPassword = c.password ?? "";
        }

        // ── EncoderConfig <-> settings ─────────────────────────────────────────────

        public EncoderConfig ToEncoderConfig() => new EncoderConfig
        {
            resolution      = ParseEnum(Resolution, EncoderConfig.Resolution.P720),
            bitrateKbps     = VideoBitrate > 0 ? VideoBitrate : 2000,
            fps             = FrameRate > 0 ? FrameRate : 30,
            rotationDegrees = RotationDegrees,
        };

        public void ApplyEncoderConfig(EncoderConfig c)
        {
            Resolution      = c.resolution.ToString();
            VideoBitrate    = c.bitrateKbps;
            FrameRate       = c.fps;
            RotationDegrees = c.rotationDegrees;
        }

        private static T ParseEnum<T>(string s, T fallback) where T : struct
        {
            return !string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out T v) ? v : fallback;
        }

        // ── Persistence ────────────────────────────────────────────────────────────

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
                        s.Normalize();
                        s.ServerPassword = Unprotect(s.ServerPasswordProtected);
                        return s;
                    }
                }
            }
            catch { }

            var defaults = new AppSettings { Uid = Guid.NewGuid().ToString() };
            defaults.Save();
            return defaults;
        }

        /// <summary>
        /// DataContractJsonSerializer zeroes/nulls members missing from the file rather
        /// than using field initializers, so patch anything that came back empty.
        /// </summary>
        private void Normalize()
        {
            if (string.IsNullOrEmpty(Uid))          Uid = Guid.NewGuid().ToString();
            if (string.IsNullOrEmpty(Alias))        Alias = "VIDEO_1";
            if (string.IsNullOrEmpty(Destination))  Destination = "SERVER";
            if (string.IsNullOrEmpty(PushProtocol)) PushProtocol = "RTSP";
            if (string.IsNullOrEmpty(StreamPath))   StreamPath = "icu";
            if (string.IsNullOrEmpty(Resolution))   Resolution = "P720";
            if (string.IsNullOrEmpty(VideoCodec))   VideoCodec = "H264";
            if (string.IsNullOrEmpty(AudioCodec))   AudioCodec = "AAC";
            if (string.IsNullOrEmpty(SensorName))   SensorName = "WinTAK-ICU";
            if (ServerPort   <= 0) ServerPort = 8554;
            if (VideoBitrate <= 0) VideoBitrate = 2000;
            if (FrameRate    <= 0) FrameRate = 30;
            if (AudioBitrate <= 0) AudioBitrate = 128;
            if (SensorHFov   <= 0) SensorHFov = 60.0;
            if (SensorVFov   <= 0) SensorVFov = 34.0;
        }

        public void Save()
        {
            try
            {
                ServerPasswordProtected = Protect(ServerPassword);

                string dir = Path.GetDirectoryName(SettingsPath);
                Directory.CreateDirectory(dir);
                RestrictDirectoryToCurrentUser(dir);

                using (var fs = File.Create(SettingsPath))
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(fs, this);
            }
            catch { }
        }

        // ── DPAPI helpers ───────────────────────────────────────────────────────────

        private static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try
            {
                byte[] encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
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
                    Convert.FromBase64String(ciphertext), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch { return ""; }
        }

        // ── File ACL ──────────────────────────────────────────────────────────────

        private static void RestrictDirectoryToCurrentUser(string path)
        {
            try
            {
                var di   = new DirectoryInfo(path);
                var acl  = di.GetAccessControl();
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;

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
            catch { /* best-effort hardening */ }
        }
    }
}
