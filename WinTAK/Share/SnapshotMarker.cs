using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Xml;
using ICUVideoStreamer.Cot;
using WinTak.CursorOnTarget.Services;

namespace ICUVideoStreamer.Share
{
    /// <summary>
    /// Captures the current preview frame to a PNG and drops a spot marker at the operator
    /// position noting the saved image. Mirrors the ATAK plugin's snapshot action
    /// (save frame + place a marker), using WinTAK's CoT pipeline for the marker.
    /// </summary>
    public static class SnapshotMarker
    {
        public sealed class Result
        {
            public bool   Saved;
            public bool   MarkerDropped;
            public string FilePath;
            public string Error;
        }

        /// <summary>Snapshot directory: <c>Documents\ICU-VideoStreamer\snapshots</c>.</summary>
        public static string SnapshotDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ICU-VideoStreamer", "snapshots");

        /// <summary>
        /// Encode <paramref name="frame"/> to a PNG and (when <paramref name="hasPosition"/>)
        /// drop a marker at (lat, lon) via <paramref name="sender"/>.
        /// </summary>
        public static Result Capture(
            BitmapSource frame, string callsign, string alias,
            double lat, double lon, bool hasPosition, ICotMessageSender sender)
        {
            var result = new Result();

            if (frame == null)
            {
                result.Error = "No frame to capture yet.";
                return result;
            }

            try
            {
                Directory.CreateDirectory(SnapshotDir);
                string name = "ICU_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string file = Path.Combine(SnapshotDir, name);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using (var fs = File.Create(file))
                    encoder.Save(fs);

                result.Saved    = true;
                result.FilePath = file;

                if (hasPosition && sender != null)
                {
                    try
                    {
                        string uid = "ICU-SNAP-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                        string cs  = (string.IsNullOrEmpty(alias) ? callsign : alias) + " snapshot";
                        // Long stale so the placed marker persists like a user-dropped point.
                        string xml = CotBuilder.BuildSpotMarker(
                            uid, cs, lat, lon, "ICU snapshot: " + name, staleSec: 60 * 60 * 24 * 365);
                        var doc = new XmlDocument();
                        doc.LoadXml(xml);
                        sender.Send(doc);
                        result.MarkerDropped = true;
                    }
                    catch { /* marker is best-effort; the PNG is already saved */ }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }
    }
}
