using System;

namespace ICUVideoStreamer.Serve
{
    /// <summary>
    /// Where and how the broadcast is published. Mirrors the ATAK plugin's
    /// <c>serve/MediaServerConfig.java</c> (Destination + PushProtocol + host/port/path),
    /// adapted for the WinTAK FFmpeg pipeline where the "LAN (this device)" destination
    /// publishes an MPEG-TS UDP multicast that other TAK clients on the network can open.
    /// </summary>
    public class MediaServerConfig
    {
        /// <summary>Where the broadcast goes — mirrors TAK ICU's "Destination Type".</summary>
        public enum Destination
        {
            /// <summary>UDP multicast on the local network — no server infrastructure.</summary>
            LAN,
            /// <summary>Push to a media server (MediaMTX / TAK Restreamer) which re-serves it.</summary>
            SERVER
        }

        /// <summary>Which protocol the desktop uses to PUSH to the server.</summary>
        public enum PushProtocol
        {
            RTMP,
            RTSP,
            SRT
        }

        // ── ATAK-standard MPEG-TS multicast target for the LAN destination ──────────
        public const string LanMulticastHost = "239.2.3.1";
        public const int    LanMulticastPort = 6969;

        public Destination  destination  = Destination.SERVER;
        public PushProtocol pushProtocol = PushProtocol.RTSP;

        public string alias      = "VIDEO_1";  // TAK ICU "Broadcast Alias"
        public string host       = "";         // server address (empty = LAN only)
        public string streamPath = "icu";      // server path / stream name
        public string username   = "";         // optional server credentials
        public string password   = "";
        public int    serverPort = 8554;       // the port the user publishes to

        public static int DefaultPort(PushProtocol p)
        {
            switch (p)
            {
                case PushProtocol.RTMP: return 1935;
                case PushProtocol.SRT:  return 8890;
                default:                return 8554; // RTSP
            }
        }

        /// <summary>True only when the destination is SERVER and an address is set.</summary>
        public bool PushEnabled =>
            destination == Destination.SERVER && !string.IsNullOrWhiteSpace(host);

        public string ProtocolName =>
            destination == Destination.LAN ? "UDP" : pushProtocol.ToString();

        /// <summary>The URL FFmpeg publishes to (no credentials — those are injected later).</summary>
        public string PushUrl()
        {
            if (destination == Destination.LAN)
                return "udp://" + LanMulticastHost + ":" + LanMulticastPort;

            switch (pushProtocol)
            {
                case PushProtocol.RTSP: return "rtsp://" + host + ":" + serverPort + "/" + streamPath;
                case PushProtocol.SRT:  return "srt://"  + host + ":" + serverPort + "?streamid=publish:" + streamPath;
                default:                return "rtmp://" + host + ":" + serverPort + "/" + streamPath;
            }
        }

        /// <summary>
        /// The URL other users open to view the stream — advertised in the sensor CoT.
        /// Contains no credentials. For a generic backend publish==view for RTSP/RTMP.
        /// </summary>
        public string ViewUrl()
        {
            if (destination == Destination.LAN)
                return "udp://" + LanMulticastHost + ":" + LanMulticastPort;

            switch (pushProtocol)
            {
                case PushProtocol.RTSP: return "rtsp://" + host + ":" + serverPort + "/" + streamPath;
                case PushProtocol.SRT:  return "srt://"  + host + ":" + serverPort + "?streamid=read:" + streamPath;
                default:                return "rtmp://" + host + ":" + serverPort + "/" + streamPath;
            }
        }

        /// <summary>
        /// The publish URL with any configured credentials injected the way FFmpeg needs
        /// them per protocol: RTSP uses userinfo (rtsp://user:pass@host), everything else
        /// uses MediaMTX's query-string form (?user=X&amp;pass=Y).
        /// </summary>
        public string PushUrlWithCredentials()
        {
            string url = PushUrl();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return url;

            if (destination == Destination.SERVER && pushProtocol == PushProtocol.RTSP)
            {
                int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
                if (schemeEnd >= 0)
                {
                    string userInfo = Uri.EscapeDataString(username) + ":" +
                                      Uri.EscapeDataString(password) + "@";
                    return url.Substring(0, schemeEnd + 3) + userInfo + url.Substring(schemeEnd + 3);
                }
                return url;
            }

            // RTMP / SRT / (LAN never has credentials): MediaMTX query-string form.
            string sep = url.Contains("?") ? "&" : "?";
            return url + sep +
                   "user=" + Uri.EscapeDataString(username) +
                   "&pass=" + Uri.EscapeDataString(password);
        }

        /// <summary>True when KLV metadata can ride this destination's container
        /// (MPEG-TS over SRT or UDP). RTSP/RTMP publish cannot carry KLV from FFmpeg.</summary>
        public bool KlvCapable =>
            destination == Destination.LAN ||
            (destination == Destination.SERVER && pushProtocol == PushProtocol.SRT);
    }
}
