using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using ICUVideoStreamer.Models;
using ICUVideoStreamer.Serve;

namespace ICUVideoStreamer
{
    /// <summary>
    /// The Broadcast Settings dialog. Mirrors the ATAK plugin's settings dialog fields
    /// (alias / destination / protocol / address / port / path / credentials / resolution
    /// / frame rate / bitrate / rotation) plus WinTAK-specific encoding and KLV sensor tabs.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly AppSettings _settings;

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            LoadSettings();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Force Win32 focus — WinTAK's input manager can swallow keys from plugin dialogs.
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            Activate();
        }

        // ── Load ────────────────────────────────────────────────────────────────────

        private void LoadSettings()
        {
            TxtAlias.Text = _settings.Alias;

            CboDestination.SelectedIndex =
                string.Equals(_settings.Destination, "LAN", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            CboProtocol.SelectedIndex = ProtocolIndex(_settings.PushProtocol);
            TxtHost.Text     = _settings.ServerHost;
            TxtPort.Text     = _settings.ServerPort.ToString();
            TxtPath.Text     = _settings.StreamPath;
            TxtUsername.Text = _settings.ServerUsername;
            PwdPassword.Password = _settings.ServerPassword;

            CboResolution.SelectedIndex = ResolutionIndex(_settings.Resolution);
            CboFrameRate.Text = _settings.FrameRate.ToString();
            SldrBitrate.Value = _settings.VideoBitrate;
            TxtBitrateLabel.Text = _settings.VideoBitrate + " kbps";
            CboRotation.SelectedIndex = RotationIndex(_settings.RotationDegrees);

            SelectComboItem(CboVideoCodec, _settings.VideoCodec ?? "H264");
            TxtAudioDevice.Text = _settings.AudioDevice;
            SelectComboItem(CboAudioCodec, _settings.AudioCodec ?? "AAC");
            CboAudioBitrate.Text = _settings.AudioBitrate.ToString();
            ChkMuteAudio.IsChecked = _settings.MuteAudio;
            ChkEnableRecording.IsChecked = _settings.EnableRecording;
            TxtRecordingPath.Text = _settings.RecordingPath;
            TxtRecordingPath.IsEnabled = _settings.EnableRecording;

            TxtFfmpegPath.Text = _settings.FfmpegPath;
            TxtFfmpegPath.TextChanged += (s, e) => UpdateFfmpegResolvedLabel();
            UpdateFfmpegResolvedLabel();

            TxtSensorName.Text = _settings.SensorName;
            TxtHFov.Text = _settings.SensorHFov.ToString("0.#");
            TxtVFov.Text = _settings.SensorVFov.ToString("0.#");

            UpdateServerGroupVisibility();
        }

        // ── Apply ───────────────────────────────────────────────────────────────────

        private void ApplyToSettings()
        {
            _settings.Alias       = NonEmpty(TxtAlias.Text, "VIDEO_1");
            _settings.Destination = CboDestination.SelectedIndex == 0 ? "LAN" : "SERVER";
            _settings.PushProtocol = ProtocolFromIndex(CboProtocol.SelectedIndex);
            _settings.ServerHost   = TxtHost.Text?.Trim() ?? "";
            _settings.ServerPort   = ParseInt(TxtPort.Text, MediaServerConfig.DefaultPort(
                                        (MediaServerConfig.PushProtocol)Enum.Parse(
                                            typeof(MediaServerConfig.PushProtocol),
                                            ProtocolFromIndex(CboProtocol.SelectedIndex))));
            _settings.StreamPath   = NonEmpty(TxtPath.Text, "icu");
            _settings.ServerUsername = TxtUsername.Text ?? "";
            _settings.ServerPassword = PwdPassword.Visibility == Visibility.Visible
                ? PwdPassword.Password
                : TxtPasswordVisible.Text;
            ApplyPastedAddress();

            _settings.Resolution      = ResolutionFromIndex(CboResolution.SelectedIndex);
            if (int.TryParse(CboFrameRate.Text, out int fps)) _settings.FrameRate = fps;
            _settings.VideoBitrate    = (int)SldrBitrate.Value;
            _settings.RotationDegrees = RotationFromIndex(CboRotation.SelectedIndex);

            _settings.VideoCodec   = GetComboText(CboVideoCodec);
            _settings.AudioDevice  = TxtAudioDevice.Text ?? "";
            _settings.AudioCodec   = GetComboText(CboAudioCodec);
            if (int.TryParse(CboAudioBitrate.Text, out int ab)) _settings.AudioBitrate = ab;
            _settings.MuteAudio       = ChkMuteAudio.IsChecked == true;
            _settings.EnableRecording = ChkEnableRecording.IsChecked == true;
            _settings.RecordingPath   = TxtRecordingPath.Text ?? "";

            _settings.FfmpegPath = TxtFfmpegPath.Text ?? "";

            _settings.SensorName = NonEmpty(TxtSensorName.Text, "WinTAK-ICU");
            if (double.TryParse(TxtHFov.Text, out double hf)) _settings.SensorHFov = hf;
            if (double.TryParse(TxtVFov.Text, out double vf)) _settings.SensorVFov = vf;
        }

        /// <summary>If a full URL was pasted into the address box, split scheme/host/port/path.</summary>
        private void ApplyPastedAddress()
        {
            string h = _settings.ServerHost?.Trim() ?? "";
            if (h.Length == 0) return;
            int scheme = h.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) h = h.Substring(scheme + 3);
            int slash = h.IndexOf('/');
            if (slash >= 0)
            {
                string p = h.Substring(slash + 1).Trim();
                if (p.Length > 0) _settings.StreamPath = p;
                h = h.Substring(0, slash);
            }
            int colon = h.IndexOf(':');
            if (colon >= 0)
            {
                if (int.TryParse(h.Substring(colon + 1).Trim(), out int port)) _settings.ServerPort = port;
                h = h.Substring(0, colon);
            }
            _settings.ServerHost = h;
        }

        // ── Buttons ───────────────────────────────────────────────────────────────

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            ApplyToSettings();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CboDestination_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateServerGroupVisibility();

        private void UpdateServerGroupVisibility()
        {
            if (ServerGroup != null)
                ServerGroup.Visibility = CboDestination.SelectedIndex == 1
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CboProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtPort == null) return;
            var proto = (MediaServerConfig.PushProtocol)Enum.Parse(
                typeof(MediaServerConfig.PushProtocol), ProtocolFromIndex(CboProtocol.SelectedIndex));
            int def = MediaServerConfig.DefaultPort(proto);

            // Only overwrite the port when it is empty or a known protocol default.
            bool isDefaultPort =
                int.TryParse(TxtPort.Text, out int cur) &&
                (cur == 1935 || cur == 8554 || cur == 8890);
            if (string.IsNullOrWhiteSpace(TxtPort.Text) || isDefaultPort)
                TxtPort.Text = def.ToString();
        }

        private void SldrBitrate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBitrateLabel != null) TxtBitrateLabel.Text = ((int)e.NewValue) + " kbps";
        }

        private void ChkRecording_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtRecordingPath != null)
                TxtRecordingPath.IsEnabled = ChkEnableRecording.IsChecked == true;
        }

        private void BtnShowPassword_Click(object sender, RoutedEventArgs e)
        {
            if (PwdPassword.Visibility == Visibility.Visible)
            {
                TxtPasswordVisible.Text = PwdPassword.Password;
                PwdPassword.Visibility = Visibility.Collapsed;
                TxtPasswordVisible.Visibility = Visibility.Visible;
                BtnShowPassword.Content = "Hide";
            }
            else
            {
                PwdPassword.Password = TxtPasswordVisible.Text;
                TxtPasswordVisible.Visibility = Visibility.Collapsed;
                PwdPassword.Visibility = Visibility.Visible;
                BtnShowPassword.Content = "Show";
            }
        }

        private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "FFmpeg executable|ffmpeg.exe|All files|*.*",
                Title = "Select FFmpeg executable"
            };
            if (dlg.ShowDialog() == true) TxtFfmpegPath.Text = dlg.FileName;
        }

        private void BrowseRecording_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Select recording folder";
                dlg.SelectedPath = TxtRecordingPath.Text;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    TxtRecordingPath.Text = dlg.SelectedPath;
            }
        }

        private void UpdateFfmpegResolvedLabel()
        {
            string custom = TxtFfmpegPath.Text?.Trim();
            bool usingCustom = !string.IsNullOrEmpty(custom) && System.IO.File.Exists(custom);
            bool bundledExists = System.IO.File.Exists(AppSettings.BundledFfmpegPath);

            if (usingCustom)
                TxtFfmpegResolved.Text = "Using: " + custom;
            else if (bundledExists)
                TxtFfmpegResolved.Text = "Using bundled: " + AppSettings.BundledFfmpegPath;
            else
                TxtFfmpegResolved.Text = "Warning: bundled ffmpeg.exe not found at " + AppSettings.BundledFfmpegPath;
        }

        // ── Index <-> value mappings ────────────────────────────────────────────────

        private static int ProtocolIndex(string p)
        {
            switch ((p ?? "RTSP").ToUpperInvariant())
            {
                case "RTMP": return 0;
                case "SRT":  return 2;
                default:     return 1; // RTSP
            }
        }
        private static string ProtocolFromIndex(int i)
        {
            switch (i) { case 0: return "RTMP"; case 2: return "SRT"; default: return "RTSP"; }
        }

        private static int ResolutionIndex(string r)
        {
            switch ((r ?? "P720").ToUpperInvariant())
            {
                case "P480":  return 0;
                case "P1080": return 2;
                default:      return 1; // P720
            }
        }
        private static string ResolutionFromIndex(int i)
        {
            switch (i) { case 0: return "P480"; case 2: return "P1080"; default: return "P720"; }
        }

        private static int RotationIndex(int deg)
        {
            switch (((deg % 360) + 360) % 360) { case 90: return 1; case 180: return 2; case 270: return 3; default: return 0; }
        }
        private static int RotationFromIndex(int i)
        {
            switch (i) { case 1: return 90; case 2: return 180; case 3: return 270; default: return 0; }
        }

        // ── Small helpers ────────────────────────────────────────────────────────────

        private static string NonEmpty(string s, string def) =>
            string.IsNullOrWhiteSpace(s) ? def : s.Trim();

        private static int ParseInt(string s, int def) =>
            int.TryParse((s ?? "").Trim(), out int v) ? v : def;

        private static void SelectComboItem(ComboBox cbo, string value)
        {
            foreach (var item in cbo.Items)
            {
                string text = item is ComboBoxItem ci ? ci.Content?.ToString() : item?.ToString();
                if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
                {
                    cbo.SelectedItem = item;
                    return;
                }
            }
            cbo.SelectedIndex = 0;
        }

        private static string GetComboText(ComboBox cbo)
        {
            if (cbo.SelectedItem is ComboBoxItem ci) return ci.Content?.ToString() ?? "";
            return cbo.Text ?? "";
        }
    }
}
