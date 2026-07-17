using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ICUVideoStreamer.Util
{
    /// <summary>
    /// Small networking helpers. Mirrors the ATAK plugin's <c>util/NetworkUtils.java</c>
    /// (resolve the reachable local address for LAN URLs / status display).
    /// </summary>
    public static class NetworkUtils
    {
        /// <summary>
        /// Best-effort primary IPv4 address of this machine (the interface that would be
        /// used to reach the network), or "127.0.0.1" if none can be determined.
        /// </summary>
        public static string LocalIPv4()
        {
            try
            {
                // Prefer an "up", non-loopback interface with a routable IPv4 address.
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                             .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                         n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
