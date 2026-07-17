using System;
using System.Text;

namespace ICUVideoStreamer.Cot
{
    /// <summary>
    /// Builds the CoT XML the plugin emits. Mirrors the ATAK plugin's
    /// <c>share/StreamSensorMarker</c> CoT shape (a <c>b-m-p-s-p-loc</c> sensor marker
    /// carrying <c>&lt;__video&gt;</c> + <c>&lt;ConnectionEntry&gt;</c> + <c>&lt;sensor&gt;</c> FOV),
    /// plus a simple spot marker for snapshots.
    /// </summary>
    public static class CotBuilder
    {
        private const string TimeFmt = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>
        /// A <c>b-m-p-s-p-loc</c> sensor marker at the operator position with the live
        /// stream link embedded, so other TAK clients can tap it and open the video, and
        /// draw the FOV cone. Sent on broadcast start and refreshed on an interval; send
        /// with a short <paramref name="staleSec"/> once on stop so peers expire it.
        /// </summary>
        public static string BuildSensorEvent(
            string uid, string callsign, string streamUrl,
            double lat, double lon,
            double sensorAzimuth, double sensorElevation,
            double hfov, double vfov, string sensorModel,
            int staleSec)
        {
            DateTime now   = DateTime.UtcNow;
            DateTime stale = now.AddSeconds(staleSec);

            string protocol = GetProtocol(streamUrl);
            int    port     = GetPort(streamUrl);
            string host     = GetHost(streamUrl);
            string path     = GetUriPath(streamUrl);
            string videoUid = uid + "-v";

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendFormat(
                "<event version=\"2.0\" uid=\"{0}\" type=\"b-m-p-s-p-loc\" how=\"m-g\" " +
                "time=\"{1}\" start=\"{1}\" stale=\"{2}\">",
                XmlEsc(uid), now.ToString(TimeFmt), stale.ToString(TimeFmt));
            sb.AppendLine();
            sb.AppendFormat(
                "  <point lat=\"{0:F7}\" lon=\"{1:F7}\" hae=\"9999999.0\" ce=\"9999999.0\" le=\"9999999.0\"/>",
                lat, lon);
            sb.AppendLine();
            sb.AppendLine("  <detail>");
            sb.AppendFormat("    <contact callsign=\"{0}\" />", XmlEsc(callsign));
            sb.AppendLine();

            // Only advertise a video link when we actually have a stream URL (broadcasting).
            if (!string.IsNullOrEmpty(streamUrl))
            {
                sb.AppendFormat("    <__video url=\"{0}\" uid=\"{1}\"/>", XmlEsc(streamUrl), XmlEsc(videoUid));
                sb.AppendLine();
                sb.AppendFormat(
                    "    <ConnectionEntry networkTimeout=\"12000\" uid=\"{0}\" path=\"{1}\" " +
                    "protocol=\"{2}\" address=\"{3}\" port=\"{4}\" bufferTime=\"-1\" roverPort=\"-1\" " +
                    "rtspReliable=\"0\" ignoreEmbeddedKLV=\"false\" alias=\"{5}\"/>",
                    XmlEsc(videoUid), XmlEsc(path), XmlEsc(protocol), XmlEsc(host), port, XmlEsc(callsign));
                sb.AppendLine();
            }

            // <device> mirrors the raw platform orientation (matches ICU IMU output).
            sb.AppendFormat("    <device azimuth=\"{0:F4}\" pitch=\"{1:F4}\" />",
                sensorAzimuth, sensorElevation);
            sb.AppendLine();
            // <sensor> drives the FOV cone colour and direction on the map.
            sb.AppendFormat(
                "    <sensor displayMagneticReference=\"0\" fov=\"{0:F4}\" " +
                "fovRed=\"1.0\" fovGreen=\"1.0\" fovBlue=\"1.0\" " +
                "azimuth=\"{1:F4}\" range=\"100.0\" />",
                hfov, sensorAzimuth);
            sb.AppendLine();
            sb.AppendLine("  </detail>");
            sb.Append("</event>");
            return sb.ToString();
        }

        /// <summary>
        /// A generic <c>b-m-p-s-m</c> spot marker at the given position, used to mark a
        /// snapshot location. <paramref name="remarks"/> notes the saved image file.
        /// </summary>
        public static string BuildSpotMarker(
            string uid, string callsign, double lat, double lon, string remarks, int staleSec)
        {
            DateTime now   = DateTime.UtcNow;
            DateTime stale = now.AddSeconds(staleSec);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.AppendFormat(
                "<event version=\"2.0\" uid=\"{0}\" type=\"b-m-p-s-m\" how=\"h-g-i-g-o\" " +
                "time=\"{1}\" start=\"{1}\" stale=\"{2}\">",
                XmlEsc(uid), now.ToString(TimeFmt), stale.ToString(TimeFmt));
            sb.AppendLine();
            sb.AppendFormat(
                "  <point lat=\"{0:F7}\" lon=\"{1:F7}\" hae=\"9999999.0\" ce=\"9999999.0\" le=\"9999999.0\"/>",
                lat, lon);
            sb.AppendLine();
            sb.AppendLine("  <detail>");
            sb.AppendFormat("    <contact callsign=\"{0}\" />", XmlEsc(callsign));
            sb.AppendLine();
            if (!string.IsNullOrEmpty(remarks))
            {
                sb.AppendFormat("    <remarks>{0}</remarks>", XmlEsc(remarks));
                sb.AppendLine();
            }
            sb.AppendLine("  </detail>");
            sb.Append("</event>");
            return sb.ToString();
        }

        // ── URL helpers ────────────────────────────────────────────────────────────

        private static string GetProtocol(string url)
        {
            if (string.IsNullOrEmpty(url)) return "rtsp";
            if (url.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)) return "rtmp";
            if (url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) return "rtsp";
            if (url.StartsWith("srt",  StringComparison.OrdinalIgnoreCase)) return "srt";
            if (url.StartsWith("udp",  StringComparison.OrdinalIgnoreCase)) return "raw"; // ATAK maps raw MPEG-TS/UDP
            return "rtsp";
        }

        private static int GetPort(string url)
        {
            try { var uri = new Uri(url); if (uri.Port > 0) return uri.Port; } catch { }
            return -1;
        }

        private static string GetHost(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        private static string GetUriPath(string url)
        {
            try { return new Uri(url).AbsolutePath; } catch { return ""; }
        }

        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
