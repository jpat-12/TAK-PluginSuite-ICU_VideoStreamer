using System;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using ICUVideoStreamer.Cot;
using WinTak.CursorOnTarget.Services;

namespace ICUVideoStreamer.Share
{
    /// <summary>
    /// Drops a CoT <b>sensor marker</b> at the operator position carrying the live stream
    /// URL and FOV cone, and re-dispatches it on an interval so the marker stays fresh and
    /// tracks the heading/elevation. On stop it emits a final short-stale event so peers
    /// expire it. Mirrors the ATAK plugin's <c>share/StreamSensorMarker.java</c>, using
    /// WinTAK's native CoT pipeline (<see cref="ICotMessageSender"/>) instead of the ATAK
    /// internal/external dispatchers.
    /// </summary>
    public sealed class StreamSensorMarker
    {
        private const int IntervalMs = 5000;
        private const int StaleSec   = 20;

        private readonly ICotMessageSender _sender;
        private readonly DispatcherTimer   _timer;

        // Identity
        private string _uid = "ICU-SENSOR";
        private string _callsign = "WinTAK-ICU";
        private string _sensorName = "WinTAK-ICU";
        private double _hfov = 60, _vfov = 34;

        // Pose
        private double _lat, _lon, _alt, _azimuth, _elevation;

        // Stream (empty when not broadcasting)
        private string _streamUrl = "";

        private bool _active;

        public StreamSensorMarker(ICotMessageSender sender)
        {
            _sender = sender;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
            _timer.Tick += (s, e) => Dispatch(StaleSec);
        }

        public void SetIdentity(string uid, string callsign, string sensorName, double hfov, double vfov)
        {
            if (!string.IsNullOrEmpty(uid)) _uid = uid;
            if (!string.IsNullOrEmpty(callsign)) _callsign = callsign;
            if (!string.IsNullOrEmpty(sensorName)) _sensorName = sensorName;
            _hfov = hfov; _vfov = vfov;
        }

        public void SetPose(double lat, double lon, double alt, double azimuth, double elevation)
        {
            _lat = lat; _lon = lon; _alt = alt; _azimuth = azimuth; _elevation = elevation;
            if (_active) Dispatch(StaleSec); // reflect slider/GPS moves immediately
        }

        public void SetStreamUrl(string url)
        {
            _streamUrl = url ?? "";
            if (_active) Dispatch(StaleSec);
        }

        /// <summary>Begin dropping/refreshing the sensor marker.</summary>
        public void Start()
        {
            _active = true;
            Dispatch(StaleSec);
            _timer.Start();
        }

        /// <summary>Stop refreshing and let peers expire the marker.</summary>
        public void Stop()
        {
            if (!_active) return;
            _active = false;
            _timer.Stop();
            Dispatch(1); // final short-stale event
        }

        private void Dispatch(int staleSec)
        {
            try
            {
                string xml = CotBuilder.BuildSensorEvent(
                    _uid, _callsign, _streamUrl,
                    _lat, _lon, _azimuth, _elevation,
                    _hfov, _vfov, _sensorName, staleSec);
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                _sender.Send(doc);
            }
            catch { /* best-effort */ }
        }
    }
}
