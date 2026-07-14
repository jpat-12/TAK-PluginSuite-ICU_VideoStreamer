using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using ICUVideoStreamer.Models;

namespace ICUVideoStreamer
{
    public partial class SettingsWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly AppSettings _settings;

        private static readonly string[] ProtocolDefaults =
        {
            "rtmp://localhost/live",         // RTMP
            "rtmps://localhost/live",        // RTMPS
            "rtsp://localhost:8554/stream",  // RTSP
            "rtsps://localhost:8554/stream", // RTSPS
            "srt://localhost:9000",          // SRT
            "udp://239.2.3.1:6969",          // UDP
        };

        private static readonly string[] Protocols =
            { "RTMP", "RTMPS", "RTSP", "RTSPS", "SRT", "UDP" };

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            LoadSettings();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Force Win32 keyboard focus to this window — WinTAK's input manager
            // can swallow key events from hosted plugin dialogs without this.
            var helper = new WindowInteropHelper(this);
            SetForegroundWindow(helper.Handle);
            Activate();
        }

        private void LoadSettings()
        {
            // Stream tab
            int protoIdx = Array.IndexOf(Protocols, (_settings.StreamProtocol ?? "RTMP").ToUpperInvariant());
            CboProtocol.SelectedIndex = protoIdx >= 0 ? protoIdx : 0;
            TxtStreamUrl.Text = _settings.StreamUrl;
            TxtStreamKey.Text = _settings.StreamKey;
            TxtUsername.Text = _settings.StreamUsername;
            PwdPassword.Password = _settings.StreamPassword;
            TxtFfmpegPath.Text = _settings.FfmpegPath;
            TxtFfmpegPath.TextChanged += (s, e) => UpdateFfmpegResolvedLabel();
            UpdateFfmpegResolvedLabel();
            ChkUseTls.IsChecked = _settings.UseTls;
            TxtCertPath.Text = _settings.CertificatePath;

            // Video tab
            SelectComboItem(CboVideoCodec, _settings.VideoCodec ?? "H264");
            CboResolution.Text = _settings.Resolution ?? "1280x720";
            CboFrameRate.Text = _settings.FrameRate.ToString();
            SldrBitrate.Value = _settings.VideoBitrate;
            TxtBitrateLabel.Text = _settings.VideoBitrate + " kbps";
            ChkEnableRecording.IsChecked = _settings.EnableRecording;
            TxtRecordingPath.Text = _settings.RecordingPath;
            ChkScreenShare.IsChecked = _settings.EnableScreenShare;

            // Audio tab
            TxtAudioDevice.Text = _settings.AudioDevice;
            SelectComboItem(CboAudioCodec, _settings.AudioCodec ?? "AAC");
            CboAudioBitrate.Text = _settings.AudioBitrate.ToString();
            ChkMuteAudio.IsChecked = _settings.MuteAudio;
        }

        private void ApplyToSettings()
        {
            // Stream
            _settings.StreamProtocol = GetComboText(CboProtocol);
            _settings.StreamUrl = TxtStreamUrl.Text;
            _settings.StreamKey = TxtStreamKey.Text;
            _settings.StreamUsername = TxtUsername.Text;
            _settings.StreamPassword = PwdPassword.Visibility == Visibility.Visible
                ? PwdPassword.Password
                : TxtPasswordVisible.Text;
            _settings.FfmpegPath = TxtFfmpegPath.Text;
            _settings.UseTls = ChkUseTls.IsChecked == true;
            _settings.CertificatePath = TxtCertPath.Text;

            // Video
            _settings.VideoCodec = GetComboText(CboVideoCodec);
            _settings.Resolution = CboResolution.Text;
            if (int.TryParse(CboFrameRate.Text, out int fps)) _settings.FrameRate = fps;
            _settings.VideoBitrate = (int)SldrBitrate.Value;
            _settings.EnableRecording = ChkEnableRecording.IsChecked == true;
            _settings.RecordingPath = TxtRecordingPath.Text;
            _settings.EnableScreenShare = ChkScreenShare.IsChecked == true;

            // Audio
            _settings.AudioDevice = TxtAudioDevice.Text;
            _settings.AudioCodec = GetComboText(CboAudioCodec);
            if (int.TryParse(CboAudioBitrate.Text, out int ab)) _settings.AudioBitrate = ab;
            _settings.MuteAudio = ChkMuteAudio.IsChecked == true;
        }

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

        private static readonly string[] Schemes =
            { "rtmp://", "rtmps://", "rtsp://", "rtsps://", "srt://", "udp://" };

        // Default ports per protocol index — 0 means no port in URL (scheme default)
        private static readonly int[] DefaultPorts = { 1935, 443, 8554, 8554, 9000, 6969 };

        private void CboProtocol_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TxtStreamUrl == null) return;
            int idx = CboProtocol.SelectedIndex;
            if (idx < 0 || idx >= ProtocolDefaults.Length) return;

            string current = TxtStreamUrl.Text ?? "";

            if (string.IsNullOrWhiteSpace(current))
            {
                TxtStreamUrl.Text = ProtocolDefaults[idx];
                return;
            }

            // Swap scheme prefix, then fix the port
            foreach (string scheme in Schemes)
            {
                if (current.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    string withoutScheme = current.Substring(scheme.Length);
                    TxtStreamUrl.Text = Schemes[idx] + SwapPort(withoutScheme, DefaultPorts[idx]);
                    return;
                }
            }

            // No recognised scheme — replace the whole URL with the default
            TxtStreamUrl.Text = ProtocolDefaults[idx];
        }

        // Replaces a known default port in the authority section of a scheme-stripped URL,
        // or appends the new port if none was present. Custom (unknown) ports are left alone.
        private static string SwapPort(string withoutScheme, int newPort)
        {
            // Split authority from path
            int slashPos = withoutScheme.IndexOf('/');
            string authority = slashPos >= 0 ? withoutScheme.Substring(0, slashPos) : withoutScheme;
            string pathEtc   = slashPos >= 0 ? withoutScheme.Substring(slashPos)    : "";

            // Separate userinfo (user:pass@) from host[:port]
            int atPos    = authority.LastIndexOf('@');
            string userInfo = atPos >= 0 ? authority.Substring(0, atPos + 1) : "";
            string hostPort = atPos >= 0 ? authority.Substring(atPos + 1)    : authority;

            // Detect existing port
            int colonPos   = hostPort.LastIndexOf(':');
            string host    = colonPos >= 0 ? hostPort.Substring(0, colonPos) : hostPort;
            string portStr = colonPos >= 0 ? hostPort.Substring(colonPos + 1) : "";

            bool hasKnownPort = int.TryParse(portStr, out int existingPort)
                                && Array.IndexOf(DefaultPorts, existingPort) >= 0;
            bool hasNoPort    = string.IsNullOrEmpty(portStr);

            if (hasKnownPort || hasNoPort)
                // Replace with new protocol's default
                return userInfo + host + ":" + newPort + pathEtc;

            // Custom port — leave it untouched, just keep the existing authority
            return userInfo + hostPort + pathEtc;
        }

        private void SldrBitrate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBitrateLabel != null)
                TxtBitrateLabel.Text = ((int)e.NewValue) + " kbps";
        }

        private void ChkRecording_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtRecordingPath != null)
                TxtRecordingPath.IsEnabled = ChkEnableRecording.IsChecked == true;
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
            if (dlg.ShowDialog() == true)
                TxtFfmpegPath.Text = dlg.FileName;
        }

        private void BrowseCert_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Certificate files|*.pem;*.crt;*.cer;*.p12;*.pfx|All files|*.*",
                Title = "Select Certificate"
            };
            if (dlg.ShowDialog() == true)
                TxtCertPath.Text = dlg.FileName;
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

        private static void SelectComboItem(System.Windows.Controls.ComboBox cbo, string value)
        {
            foreach (var item in cbo.Items)
            {
                string text = item is System.Windows.Controls.ComboBoxItem ci
                    ? ci.Content?.ToString() : item?.ToString();
                if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
                {
                    cbo.SelectedItem = item;
                    return;
                }
            }
            cbo.SelectedIndex = 0;
        }

        private static string GetComboText(System.Windows.Controls.ComboBox cbo)
        {
            if (cbo.SelectedItem is System.Windows.Controls.ComboBoxItem ci)
                return ci.Content?.ToString() ?? "";
            return cbo.Text ?? "";
        }
    }
}
