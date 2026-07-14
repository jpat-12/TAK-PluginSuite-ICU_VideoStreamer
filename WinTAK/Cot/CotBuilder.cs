using System;
using System.Text;

namespace ICUVideoStreamer.Cot
{
    public static class CotBuilder
    {
        private const string TimeFmt = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>
        /// Builds a b-m-p-s-p-loc sensor/SA CoT event matching the format sent by
        /// the TAK ICU Android app.  This type causes WinTAK / ATAK to draw the
        /// camera FOV cone on the map.  The video stream URL is embedded in the
        /// detail so clients can open the stream directly from the marker.
        ///
        /// Send once at stream-start and every ~10 s while streaming so the cone
        /// stays alive (stale = 60 s) and tracks the heading/elevation sliders.
        /// </summary>
        public static string BuildVideoEvent(
            string uid, string callsign, string streamUrl,
            double lat, double lon,
            double sensorAzimuth   = 0,   // compass bearing camera is pointing (0–360°)
            double sensorElevation = 0,   // tilt from horizontal: negative = looking down
            double hfov            = 60,  // horizontal field of view, degrees
            double vfov            = 34,  // vertical field of view, degrees (kept for KLV)
            string sensorModel     = "WinTAK-ICU")
        {
            DateTime now   = DateTime.UtcNow;
            DateTime stale = now.AddSeconds(60); // 6× the 10-second refresh interval

            string protocol = GetProtocol(streamUrl);
            int    port     = GetPort(streamUrl);
            string host     = GetHost(streamUrl);
            string path     = GetUriPath(streamUrl);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            // Use b-m-p-s-p-loc (sensor/person marker) — same as TAK ICU.
            // This type triggers the FOV cone overlay on the map.
            sb.AppendFormat(
                "<event version=\"2.0\" uid=\"{0}\" type=\"b-m-p-s-p-loc\" how=\"h-e\" " +
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
            // Video stream link — uid keeps -VIDEO suffix so the stream object is
            // separate from the marker object on the WinTAK map.
            sb.AppendFormat("    <__video url=\"{0}\" uid=\"{1}-VIDEO\"/>", XmlEsc(streamUrl), XmlEsc(uid));
            sb.AppendLine();
            // ConnectionEntry: address = hostname only, path = URI path — matches ICU format.
            sb.AppendFormat(
                "    <ConnectionEntry networkTimeout=\"12000\" uid=\"{0}-VIDEO\" path=\"{1}\" " +
                "protocol=\"{2}\" address=\"{3}\" port=\"{4}\" bufferTime=\"-1\" roverPort=\"-1\" " +
                "rtspReliable=\"0\" ignoreEmbeddedKLV=\"false\" alias=\"{5}\"/>",
                XmlEsc(uid), XmlEsc(path), XmlEsc(protocol), XmlEsc(host), port, XmlEsc(callsign));
            sb.AppendLine();
            // <device> mirrors the raw platform orientation — same as ICU phone IMU output.
            sb.AppendFormat(
                "    <device azimuth=\"{0:F4}\" pitch=\"{1:F4}\" />",
                sensorAzimuth, sensorElevation);
            sb.AppendLine();
            // <sensor> drives the FOV cone colour and direction on the WinTAK map.
            // displayMagneticReference="0" = true north reference.
            // fovRed/Green/Blue = cone colour (1,1,1 = white, same as ICU default).
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

        private static string GetProtocol(string url)
        {
            if (string.IsNullOrEmpty(url)) return "rtsp";
            if (url.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase)) return "rtmp";
            if (url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) return "rtsp";
            if (url.StartsWith("srt",  StringComparison.OrdinalIgnoreCase)) return "srt";
            if (url.StartsWith("udp",  StringComparison.OrdinalIgnoreCase)) return "udp";
            return "rtsp";
        }

        private static int GetPort(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (uri.Port > 0) return uri.Port;
            }
            catch { }
            return -1;
        }

        private static string GetHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }

        private static string GetUriPath(string url)
        {
            try { return new Uri(url).AbsolutePath; }
            catch { return ""; }
        }

        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
