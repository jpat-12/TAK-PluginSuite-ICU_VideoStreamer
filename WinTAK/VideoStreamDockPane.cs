using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.Commands;
using WinTak.Common.Location;
using WinTak.Common.Services;
using WinTak.CursorOnTarget.Services;
using WinTak.Framework.Docking;
using WinTak.Framework.Docking.Attributes;
using ICUVideoStreamer.Capture;
using ICUVideoStreamer.Klv;
using ICUVideoStreamer.Models;
using ICUVideoStreamer.Serve;
using ICUVideoStreamer.Share;

namespace ICUVideoStreamer
{
    /// <summary>
    /// The operator pane. Mirrors the ATAK plugin's <c>ICUVideoDropDownReceiver</c>:
    /// a docked UAS-style HUD (status row + destination badge + video + right-edge
    /// Broadcast/Record/Snapshot quick-action column) with start/stop confirmations.
    /// The WinTAK power features (camera picker, GPS, sensor FOV, manual location) live
    /// in a collapsible control drawer so the default look matches the ATAK operator pane.
    /// </summary>
    [DockPane(ID, "ICU VideoStreamer", Content = typeof(VideoStreamView))]
    [Export(typeof(IDockPane))]
    public class VideoStreamDockPane : DockPane
    {
        internal const string ID = "ICUVideoStreamer_VideoStreamDockPane";

        // WinTAK-injected services
        private readonly ILocationService  _locationService;
        private readonly ICotMessageSender _cotSender;

        // Plugin-owned services
        private AppSettings          _settings;
        private MediaServerConfig    _server;
        private EncoderConfig        _encoder;
        private StreamingService     _streamingService;
        private CameraPreviewService _previewService;
        private KlvService           _klvService;
        private StreamSensorMarker   _sensorMarker;

        // Timers
        private DispatcherTimer _clockTimer;
        private DispatcherTimer _streamTimer;
        private DateTime        _streamStart;

        // Location
        private double _lat, _lon, _alt;
        private bool   _hasPosition;
        private bool   _streamPathResolved; // stream path has been seeded from the callsign (or already custom)

        [ImportingConstructor]
        public VideoStreamDockPane(ILocationService locationService, ICotMessageSender cotSender)
        {
            _locationService = locationService;
            _cotSender       = cotSender;

            BroadcastCommand            = new DelegateCommand(OnToggleBroadcast);
            RecordCommand               = new DelegateCommand(OnToggleRecord);
            SnapshotCommand             = new DelegateCommand(OnSnapshot);
            OpenSettingsCommand         = new DelegateCommand(OnOpenSettings);
            RefreshDevicesCommand       = new DelegateCommand(OnRefreshDevices);
            ToggleControlPanelCommand   = new DelegateCommand(() => ControlPanelVisible = !ControlPanelVisible);
            ApplyLocationCommand        = new DelegateCommand(OnApplyLocation);
            ToggleManualLocationCommand = new DelegateCommand(() =>
                IsManualLocationExpanded = !IsManualLocationExpanded);

            _settings = AppSettings.Load();
            _sensorMarker = new StreamSensorMarker(_cotSender);

            InitializeServices();
            StartClockTimer();
            OnRefreshDevices();
            RefreshDestinationBadge();
            RefreshStreamUrlDisplay();
            TrySeedStreamPathFromCallsign();   // immediate attempt; clock timer retries until callsign is known

            // Seed sensor sliders from persisted defaults
            _sensorHeading   = 0;
            _sensorElevation = 0;

            // Subscribe to WinTAK GPS
            _locationService.PositionChanged         += OnPositionChanged;
            _locationService.ConnectionStatusChanged += OnGpsConnectionChanged;
            SyncPositionFromService();
            UpdateGpsStatus();
        }

        // ══ Observable properties ═══════════════════════════════════════════════════

        private string _statusText = "Idle — not broadcasting";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        private string _destinationBadge = "LAN";
        public string DestinationBadge { get => _destinationBadge; private set => SetProperty(ref _destinationBadge, value); }

        private string _clockText = "";
        public string ClockText { get => _clockText; private set => SetProperty(ref _clockText, value); }

        private string _bitrateText = "";
        public string BitrateText { get => _bitrateText; private set => SetProperty(ref _bitrateText, value); }

        private string _streamElapsedText = "";
        public string StreamElapsedText { get => _streamElapsedText; private set => SetProperty(ref _streamElapsedText, value); }

        private bool _isStreaming;
        public bool IsStreaming
        {
            get => _isStreaming;
            private set
            {
                if (SetProperty(ref _isStreaming, value))
                {
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanChangeSource)));
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(BroadcastLabel)));
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(LiveDotVisibility)));
                }
            }
        }

        public string BroadcastLabel => IsStreaming ? "Stop" : "Broadcast";
        public Visibility LiveDotVisibility => IsStreaming ? Visibility.Visible : Visibility.Collapsed;
        public bool CanChangeSource => !IsStreaming;

        private bool _recordArmed;
        public bool RecordArmed
        {
            get => _recordArmed;
            private set
            {
                if (SetProperty(ref _recordArmed, value))
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(RecBadgeVisibility)));
            }
        }

        public Visibility RecBadgeVisibility =>
            (RecordArmed || (IsStreaming && _settings.EnableRecording))
                ? Visibility.Visible : Visibility.Collapsed;

        private ImageSource _frozenPreviewBitmap;
        private bool _streamPreviewActive; // true once the streaming FFmpeg's teed preview is rendering

        public ImageSource PreviewBitmap =>
            (IsStreaming && _streamPreviewActive)
                ? (ImageSource)_streamingService?.PreviewBitmap ?? _frozenPreviewBitmap
                : (_previewService?.Bitmap ?? _frozenPreviewBitmap);

        private double _previewRotation;
        public double PreviewRotation { get => _previewRotation; private set => SetProperty(ref _previewRotation, value); }

        private string _streamUrlDisplay = "";
        public string StreamUrlDisplay { get => _streamUrlDisplay; private set => SetProperty(ref _streamUrlDisplay, value); }

        public string BuildDateText { get; } =
            "Built " + System.IO.File.GetLastWriteTime(
                System.Reflection.Assembly.GetExecutingAssembly().Location).ToString("yyyy-MM-dd HH:mm");

        private bool _controlPanelVisible;
        public bool ControlPanelVisible { get => _controlPanelVisible; set => SetProperty(ref _controlPanelVisible, value); }

        // GPS
        private Brush _gpsDotColor = Brushes.Red;
        public Brush GpsDotColor { get => _gpsDotColor; private set => SetProperty(ref _gpsDotColor, value); }

        private string _gpsStatusText = "No GPS";
        public string GpsStatusText { get => _gpsStatusText; private set => SetProperty(ref _gpsStatusText, value); }

        private string _latText = "0.0000000";
        public string LatText { get => _latText; private set => SetProperty(ref _latText, value); }

        private string _lonText = "0.0000000";
        public string LonText { get => _lonText; private set => SetProperty(ref _lonText, value); }

        private string _altText = "0.0";
        public string AltText { get => _altText; private set => SetProperty(ref _altText, value); }

        public string PositionText => $"{LatText}, {LonText}";

        // Camera
        public ObservableCollection<string> CameraDevices { get; } = new ObservableCollection<string>();

        private string _selectedCamera = "";
        public string SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                if (SetProperty(ref _selectedCamera, value))
                {
                    _settings.VideoDevice = value ?? "";
                    UpdatePreview();
                }
            }
        }

        private bool _isScreenShare;
        public bool IsScreenShare
        {
            get => _isScreenShare;
            set
            {
                if (SetProperty(ref _isScreenShare, value))
                {
                    _settings.EnableScreenShare = value;
                    UpdatePreview();
                }
            }
        }

        // Sensor sliders (KLV tag 5 heading / tag 19 elevation + CoT sensor azimuth)
        private double _sensorHeading;
        public double SensorHeading
        {
            get => _sensorHeading;
            set
            {
                if (SetProperty(ref _sensorHeading, value))
                {
                    _klvService?.UpdateHeading(value);
                    _sensorMarker?.SetPose(_lat, _lon, _alt, _sensorHeading, _sensorElevation);
                }
            }
        }

        private double _sensorElevation;
        public double SensorElevation
        {
            get => _sensorElevation;
            set
            {
                if (SetProperty(ref _sensorElevation, value))
                {
                    _klvService?.UpdateElevation(value);
                    _sensorMarker?.SetPose(_lat, _lon, _alt, _sensorHeading, _sensorElevation);
                }
            }
        }

        // Manual location
        private string _manualLat = "";
        public string ManualLat { get => _manualLat; set => SetProperty(ref _manualLat, value); }
        private string _manualLon = "";
        public string ManualLon { get => _manualLon; set => SetProperty(ref _manualLon, value); }
        private string _manualAlt = "";
        public string ManualAlt { get => _manualAlt; set => SetProperty(ref _manualAlt, value); }

        private bool _isManualLocationExpanded;
        public bool IsManualLocationExpanded { get => _isManualLocationExpanded; set => SetProperty(ref _isManualLocationExpanded, value); }

        // ══ Commands ════════════════════════════════════════════════════════════════

        public DelegateCommand BroadcastCommand            { get; }
        public DelegateCommand RecordCommand               { get; }
        public DelegateCommand SnapshotCommand             { get; }
        public DelegateCommand OpenSettingsCommand         { get; }
        public DelegateCommand RefreshDevicesCommand       { get; }
        public DelegateCommand ToggleControlPanelCommand   { get; }
        public DelegateCommand ApplyLocationCommand        { get; }
        public DelegateCommand ToggleManualLocationCommand { get; }

        // ══ Service setup ═══════════════════════════════════════════════════════════

        private void InitializeServices()
        {
            _streamingService?.Dispose();

            _server  = _settings.ToServerConfig();
            _encoder = _settings.ToEncoderConfig();
            PreviewRotation = _encoder.rotationDegrees;

            _streamingService = new StreamingService(_settings);
            _streamingService.BitrateUpdated += kbps =>
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    BitrateText = kbps > 0 ? kbps + " kbps" : "");
            _streamingService.StatusChanged += s =>
                Application.Current?.Dispatcher.InvokeAsync(() => StatusText = s);
            _streamingService.Stopped += () =>
                Application.Current?.Dispatcher.InvokeAsync(OnStreamStopped);
            _streamingService.PreviewFrameReady += () =>
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _streamPreviewActive = true;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(PreviewBitmap)));
                });

            if (_klvService == null) _klvService = new KlvService();
            _klvService.SensorName = _settings.SensorName;
            _klvService.UpdateFov(_settings.SensorHFov, _settings.SensorVFov);
            _klvService.UpdateHeading(_sensorHeading);
            _klvService.UpdateElevation(_sensorElevation);
            _klvService.UpdateLocation(_lat, _lon, _alt);

            var (uid, callsign) = GetSelfIdentity();
            _sensorMarker.SetIdentity(uid + "-SENSOR", callsign, _settings.SensorName,
                _settings.SensorHFov, _settings.SensorVFov);
        }

        private void StartClockTimer()
        {
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) =>
            {
                ClockText = DateTime.Now.ToString("HH:mm:ss");
                if (!_streamPathResolved) TrySeedStreamPathFromCallsign();
            };
            _clockTimer.Start();
        }

        // ══ Preview ═════════════════════════════════════════════════════════════════

        private void UpdatePreview()
        {
            bool shouldPreview = !string.IsNullOrEmpty(SelectedCamera) && !IsScreenShare && !IsStreaming;

            _previewService?.Stop();
            if (shouldPreview)
            {
                _previewService = new CameraPreviewService();
                _previewService.StatusChanged += s =>
                    Application.Current?.Dispatcher.InvokeAsync(() => StatusText = s);
                _previewService.Start(SelectedCamera, _settings.ResolvedFfmpegPath, Application.Current.Dispatcher);
            }
            else
            {
                _previewService = null;
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(PreviewBitmap)));
        }

        // ══ Broadcast ═══════════════════════════════════════════════════════════════

        private void OnToggleBroadcast()
        {
            if (IsStreaming)
            {
                if (Confirm("Stop Broadcast?",
                        "This stops the camera and drops the feed for anyone watching.",
                        "Stop"))
                    StopBroadcast();
                return;
            }

            string dest = _server.PushEnabled
                ? _server.ProtocolName + " → " + _server.ViewUrl()
                : "Local network (UDP multicast " +
                  MediaServerConfig.LanMulticastHost + ":" + MediaServerConfig.LanMulticastPort + ")";

            if (Confirm("Start Broadcast?", "Destination:\n" + dest, "Start"))
                StartBroadcast();
        }

        private void StartBroadcast()
        {
            if (string.IsNullOrEmpty(SelectedCamera) && !IsScreenShare)
            {
                StatusText = "Select a camera or enable Screen Share first.";
                return;
            }

            _frozenPreviewBitmap = _previewService?.Bitmap; // keep last frame while streaming
            _previewService?.Stop();
            _previewService = null;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(PreviewBitmap)));

            // KLV pipe must be listening before FFmpeg connects.
            _klvService?.UpdateLocation(_lat, _lon, _alt);
            _klvService?.UpdateHeading(_sensorHeading);
            _klvService?.UpdateElevation(_sensorElevation);
            if (_server.KlvCapable) _klvService?.Start();

            // Give DirectShow time to release the camera before FFmpeg opens it.
            System.Threading.Tasks.Task.Delay(600).ContinueWith(_ =>
                Application.Current?.Dispatcher.InvokeAsync(StartBroadcastCore));
        }

        private void StartBroadcastCore()
        {
            string klvPipe = _server.KlvCapable ? _klvService?.PipeName : null;
            _streamPreviewActive = false; // show frozen frame until the teed preview delivers
            _streamingService.Start(_server, _encoder, klvPipe, Application.Current?.Dispatcher);
            if (!_streamingService.IsStreaming)
            {
                _klvService?.Stop();
                return;
            }

            IsStreaming       = true;
            StatusText        = "LIVE · " + EncoderConfig.Label(_encoder.resolution);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(RecBadgeVisibility)));
            RefreshStreamUrlDisplay();
            StartStreamTimer();

            // Sensor marker carries the reachable view URL.
            _sensorMarker.SetPose(_lat, _lon, _alt, _sensorHeading, _sensorElevation);
            _sensorMarker.SetStreamUrl(_server.ViewUrl());
            _sensorMarker.Start();
        }

        private void StopBroadcast()
        {
            _streamingService.Stop();
            OnStreamStopped();
        }

        private void OnStreamStopped()
        {
            // Idempotent: StopBroadcast() calls this directly and the FFmpeg Stopped event
            // also fires it. After the first pass IsStreaming is false and the timer is gone.
            if (!IsStreaming && _streamTimer == null) return;

            _sensorMarker.Stop();
            _klvService?.Stop();

            _streamPreviewActive = false;
            IsStreaming        = false;
            BitrateText        = "";
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(PreviewBitmap)));
            StopStreamTimer();
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(RecBadgeVisibility)));
            RefreshStreamUrlDisplay();
            StatusText = "Idle — not broadcasting";

            // Let FFmpeg fully release the camera before restarting the preview.
            System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _frozenPreviewBitmap = null;
                    UpdatePreview();
                }));
        }

        // ══ Record / Snapshot ═══════════════════════════════════════════════════════

        private void OnToggleRecord()
        {
            _settings.EnableRecording = !_settings.EnableRecording;
            RecordArmed = _settings.EnableRecording;
            _settings.Save();

            if (_settings.EnableRecording && string.IsNullOrEmpty(_settings.RecordingPath))
            {
                _settings.RecordingPath = SnapshotMarker.SnapshotDir; // reuse a sane default dir
                _settings.Save();
            }

            if (IsStreaming)
                StatusText = "Recording " + (_settings.EnableRecording ? "armed" : "disarmed") +
                             " — applies on next broadcast.";
            else
                StatusText = "Recording " + (_settings.EnableRecording ? "armed" : "disarmed") + ".";
        }

        private void OnSnapshot()
        {
            var frame = PreviewBitmap as System.Windows.Media.Imaging.BitmapSource;
            var (uid, callsign) = GetSelfIdentity();
            var result = SnapshotMarker.Capture(
                frame, callsign, _settings.Alias, _lat, _lon, _hasPosition, _cotSender);

            if (!result.Saved)
                StatusText = "Snapshot failed: " + (result.Error ?? "no frame");
            else
                StatusText = result.MarkerDropped
                    ? "Snapshot saved + marker dropped"
                    : "Snapshot saved: " + System.IO.Path.GetFileName(result.FilePath);
        }

        // ══ WinTAK GPS ══════════════════════════════════════════════════════════════

        private void OnPositionChanged(object sender, PositionChangedEventArgs e)
        {
            _lat = e.Position.Latitude;
            _lon = e.Position.Longitude;
            _alt = e.Position.Altitude;
            _hasPosition = true;
            _klvService?.UpdateLocation(_lat, _lon, _alt);
            _sensorMarker?.SetPose(_lat, _lon, _alt, _sensorHeading, _sensorElevation);

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LatText = _lat.ToString("F7");
                LonText = _lon.ToString("F7");
                AltText = _alt.ToString("F1");
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(PositionText)));
                GpsDotColor   = Brushes.LimeGreen;
                GpsStatusText = "GPS Active";
            });
        }

        private void OnGpsConnectionChanged(object sender, EventArgs e) =>
            Application.Current?.Dispatcher.InvokeAsync(UpdateGpsStatus);

        private void SyncPositionFromService()
        {
            try
            {
                var pos = _locationService.GetGpsPosition();
                if (pos != null)
                {
                    _lat = pos.Latitude; _lon = pos.Longitude; _alt = pos.Altitude;
                    _hasPosition = true;
                    LatText = _lat.ToString("F7");
                    LonText = _lon.ToString("F7");
                    AltText = _alt.ToString("F1");
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(PositionText)));
                }
            }
            catch { }
        }

        private void UpdateGpsStatus()
        {
            bool hasGps = _locationService.HasConnections;
            GpsDotColor   = hasGps ? Brushes.LimeGreen : Brushes.Red;
            GpsStatusText = hasGps ? "GPS Active" : "No GPS";
        }

        private (string uid, string callsign) GetSelfIdentity()
        {
            try
            {
                var doc = _locationService.GetPositionDocument();
                if (doc?.DocumentElement != null)
                {
                    string uid = doc.DocumentElement.GetAttribute("uid");
                    var contact = doc.SelectSingleNode("//contact") as System.Xml.XmlElement;
                    string callsign = contact?.GetAttribute("callsign") ?? "";
                    if (!string.IsNullOrEmpty(uid)) return (uid, callsign);
                }
            }
            catch { }
            return (_settings.Uid, "WinTAK-ICU");
        }

        /// <summary>
        /// Read just the operator callsign from WinTAK's self position document (available
        /// once the self identity is initialized), or null if not ready yet.
        /// </summary>
        private string GetSelfCallsign()
        {
            try
            {
                var doc = _locationService.GetPositionDocument();
                var contact = doc?.SelectSingleNode("//contact") as System.Xml.XmlElement;
                string cs = contact?.GetAttribute("callsign");
                if (!string.IsNullOrWhiteSpace(cs)) return cs.Trim();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Seed the stream path/name from the operator's WinTAK callsign so two operators
        /// pushing to the same media server don't collide on a shared "icu" path. The
        /// callsign isn't available when the pane is first constructed, so this is retried
        /// from the clock timer until it succeeds. Only applied while the path is still the
        /// untouched default; a user override in Settings sticks.
        /// </summary>
        private void TrySeedStreamPathFromCallsign()
        {
            if (_streamPathResolved) return;

            // Already customized (anything other than the "icu" default) — leave it alone.
            if (!string.IsNullOrWhiteSpace(_settings.StreamPath) && _settings.StreamPath != "icu")
            {
                _streamPathResolved = true;
                return;
            }

            string callsign = GetSelfCallsign();
            if (string.IsNullOrWhiteSpace(callsign) || callsign == "WinTAK-ICU")
                return; // not ready yet — the clock timer will retry

            string sanitized = SanitizeStreamPath(callsign);
            _settings.StreamPath = sanitized;
            _settings.Save();
            if (_server != null) _server.streamPath = sanitized; // apply to the live config too
            _streamPathResolved = true;
            RefreshStreamUrlDisplay();
        }

        /// <summary>Reduce a callsign to a URL/streamid-safe path segment.</summary>
        private static string SanitizeStreamPath(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "icu";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('_');
                // everything else dropped
            }
            string r = sb.ToString().Trim('_');
            return r.Length == 0 ? "icu" : r;
        }

        // ══ Timers ══════════════════════════════════════════════════════════════════

        private void StartStreamTimer()
        {
            _streamStart = DateTime.Now;
            _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _streamTimer.Tick += (s, e) =>
                StreamElapsedText = (DateTime.Now - _streamStart).ToString(@"hh\:mm\:ss");
            _streamTimer.Start();
        }

        private void StopStreamTimer()
        {
            _streamTimer?.Stop();
            _streamTimer = null;
            StreamElapsedText = "";
        }

        // ══ Settings / devices / location ═══════════════════════════════════════════

        private void OnOpenSettings()
        {
            var win = new SettingsWindow(_settings) { Owner = Application.Current?.MainWindow };
            if (win.ShowDialog() == true)
            {
                _settings.Save();
                RecordArmed = _settings.EnableRecording;
                InitializeServices();
                RefreshDestinationBadge();
                RefreshStreamUrlDisplay();
                if (IsStreaming)
                {
                    StatusText = "Restarting with new settings…";
                    StopBroadcast();
                    // preview/stream restart is deferred; user can re-tap Broadcast
                }
                else
                {
                    UpdatePreview();
                }
            }
        }

        private void OnRefreshDevices()
        {
            var devices = StreamingService.EnumerateVideoDevices(_settings.FfmpegPath);
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CameraDevices.Clear();
                foreach (string d in devices) CameraDevices.Add(d);

                // Restore the saved device if present, else pick the first.
                if (!string.IsNullOrEmpty(_settings.VideoDevice) && CameraDevices.Contains(_settings.VideoDevice))
                    SelectedCamera = _settings.VideoDevice;
                else if (CameraDevices.Count > 0 && string.IsNullOrEmpty(SelectedCamera))
                    SelectedCamera = CameraDevices[0];
                else
                    UpdatePreview();
            });
        }

        private void OnApplyLocation()
        {
            if (double.TryParse(ManualLat, out double lat)) _lat = lat;
            if (double.TryParse(ManualLon, out double lon)) _lon = lon;
            if (double.TryParse(ManualAlt, out double alt)) _alt = alt;
            _hasPosition = true;
            LatText = _lat.ToString("F7");
            LonText = _lon.ToString("F7");
            AltText = _alt.ToString("F1");
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(PositionText)));
            _klvService?.UpdateLocation(_lat, _lon, _alt);
            _sensorMarker?.SetPose(_lat, _lon, _alt, _sensorHeading, _sensorElevation);
            StatusText = "Location overridden manually";
        }

        private void RefreshDestinationBadge()
        {
            DestinationBadge = _server.PushEnabled
                ? "SERVER → " + _server.host
                : "LAN";
        }

        private void RefreshStreamUrlDisplay()
        {
            StreamUrlDisplay = IsStreaming ? _server.ProtocolName + "  " + _server.ViewUrl() : "";
        }

        // ══ Helpers ═════════════════════════════════════════════════════════════════

        private static bool Confirm(string title, string message, string positive)
        {
            var r = MessageBox.Show(Application.Current?.MainWindow,
                message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            return r == MessageBoxResult.OK;
        }
    }
}
