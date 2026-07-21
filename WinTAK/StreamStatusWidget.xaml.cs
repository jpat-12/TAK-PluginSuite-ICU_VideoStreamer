using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ICUVideoStreamer.Models;
using WinTak.Framework.Docking;

namespace ICUVideoStreamer
{
    /// <summary>
    /// Persistent, map-anchored streaming-status indicator — the plugin icon with a small
    /// red/green status dot and a pulsing "LIVE" label while streaming. This is the WinTAK
    /// port of the ATAK <c>StreamStatusWidget</c>; ATAK renders it as a map <c>MapWidget</c>,
    /// which has no public WinTAK equivalent, so here it is a borderless, transparent overlay
    /// window owned by the WinTAK main window. Being owned means it stays above the map, moves
    /// to the taskbar with WinTAK (minimize/restore), and closes when WinTAK closes — the same
    /// "always there while the app is up" behaviour as the ATAK map widget.
    ///
    /// <para>A plain click activates the plugin's dock pane; press-and-drag relocates the badge,
    /// and the position (stored as an offset from the main window's top-left, so it tracks the
    /// window) is persisted per user. Until the user drags it, it auto-anchors to the bottom-left,
    /// matching the ATAK default of sitting in the left-side HUD cluster.</para>
    /// </summary>
    public partial class StreamStatusWidget : Window
    {
        private const double MarginX = 16;   // default left inset
        private const double MarginY = 64;   // default inset up from the bottom edge
        private const double DragThreshold = 4;

        private readonly IDockingManager _dockingManager;
        private readonly AppSettings _settings;
        private Window _owner;

        private Storyboard _pulse;
        private bool _dragging;
        private bool _dragMoved;
        private Point _dragStartScreen;
        private double _dragStartLeft;
        private double _dragStartTop;

        public StreamStatusWidget(IDockingManager dockingManager, AppSettings settings)
        {
            _dockingManager = dockingManager;
            _settings = settings;

            InitializeComponent();

            BuildPulse();
            SetStreaming(StreamStatusHub.IsStreaming);
            StreamStatusHub.StreamingChanged += OnStreamingChanged;

            Loaded += OnLoaded;
            Closed += OnClosed;

            MouseLeftButtonDown += OnMouseDown;
            MouseMove           += OnMouseMove;
            MouseLeftButtonUp   += OnMouseUp;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _owner = Owner ?? Application.Current?.MainWindow;
            if (_owner != null)
            {
                Owner = _owner;
                _owner.LocationChanged += (s, a) => Reposition();
                _owner.SizeChanged     += (s, a) => Reposition();
                _owner.StateChanged    += OnOwnerStateChanged;
                OnOwnerStateChanged(null, EventArgs.Empty);
            }
            Reposition();
        }

        private void OnOwnerStateChanged(object sender, EventArgs e)
        {
            // Follow the owner into/out of minimize so the badge doesn't float over the desktop.
            Visibility = (_owner != null && _owner.WindowState == WindowState.Minimized)
                ? Visibility.Hidden
                : Visibility.Visible;
        }

        // ── Streaming state ──────────────────────────────────────────────────────────

        private void OnStreamingChanged(bool streaming)
        {
            Dispatcher.InvokeAsync(() => SetStreaming(streaming));
        }

        private void SetStreaming(bool streaming)
        {
            StatusDot.Fill = new SolidColorBrush(
                streaming ? Color.FromRgb(0x2E, 0xCC, 0x71)   // green
                          : Color.FromRgb(0xE7, 0x4C, 0x3C)); // red
            LiveLabel.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;

            if (streaming)
                _pulse.Begin(LiveLabel, true);
            else
            {
                _pulse.Stop(LiveLabel);
                LiveLabel.Opacity = 1.0;
            }
        }

        private void BuildPulse()
        {
            var fade = new DoubleAnimation
            {
                From = 1.0,
                To = 0.30,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(fade, LiveLabel);
            Storyboard.SetTargetProperty(fade, new PropertyPath(nameof(Opacity)));
            _pulse = new Storyboard();
            _pulse.Children.Add(fade);
        }

        // ── Positioning ──────────────────────────────────────────────────────────────

        private void Reposition()
        {
            if (_owner == null) return;

            if (_settings.WidgetPositioned)
            {
                Left = _owner.Left + _settings.WidgetOffsetX;
                Top  = _owner.Top  + _settings.WidgetOffsetY;
            }
            else
            {
                // Default: bottom-left of the owner window.
                Left = _owner.Left + MarginX;
                Top  = _owner.Top  + Math.Max(0, _owner.ActualHeight - ActualHeight - MarginY);
            }
        }

        // ── Click vs. drag ───────────────────────────────────────────────────────────

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragMoved = false;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _dragStartLeft = Left;
            _dragStartTop = Top;
            CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point now = PointToScreen(e.GetPosition(this));
            double dx = now.X - _dragStartScreen.X;
            double dy = now.Y - _dragStartScreen.Y;
            if (!_dragMoved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

            _dragMoved = true;
            Left = _dragStartLeft + dx;
            Top  = _dragStartTop + dy;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            if (_dragMoved)
            {
                if (_owner != null)
                {
                    _settings.WidgetPositioned = true;
                    _settings.WidgetOffsetX = Left - _owner.Left;
                    _settings.WidgetOffsetY = Top - _owner.Top;

                    // Persist only the widget fields onto a fresh copy so we don't clobber
                    // stream/settings values another component may have changed on disk.
                    var onDisk = AppSettings.Load();
                    onDisk.WidgetPositioned = _settings.WidgetPositioned;
                    onDisk.WidgetOffsetX = _settings.WidgetOffsetX;
                    onDisk.WidgetOffsetY = _settings.WidgetOffsetY;
                    onDisk.Save();
                }
            }
            else
            {
                ActivatePane();
            }
        }

        private void ActivatePane()
        {
            try
            {
                var pane = _dockingManager?.GetDockPane(VideoStreamDockPane.ID);
                pane?.Activate();
            }
            catch { /* docking manager unavailable — nothing to open */ }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            StreamStatusHub.StreamingChanged -= OnStreamingChanged;
        }
    }
}
