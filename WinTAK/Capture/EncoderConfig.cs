namespace ICUVideoStreamer.Capture
{
    /// <summary>
    /// Capture + encode parameters for the desktop video source.
    /// Mirrors the ATAK plugin's <c>capture/EncoderConfig.java</c> (resolution /
    /// bitrate / fps / rotation), adapted for the WinTAK FFmpeg pipeline.
    /// </summary>
    public class EncoderConfig
    {
        public enum Resolution
        {
            P480,
            P720,
            P1080
        }

        public static int Width(Resolution r)
        {
            switch (r)
            {
                case Resolution.P480:  return 854;
                case Resolution.P1080: return 1920;
                default:               return 1280;
            }
        }

        public static int Height(Resolution r)
        {
            switch (r)
            {
                case Resolution.P480:  return 480;
                case Resolution.P1080: return 1080;
                default:               return 720;
            }
        }

        public static string Label(Resolution r)
        {
            switch (r)
            {
                case Resolution.P480:  return "480p";
                case Resolution.P1080: return "1080p";
                default:               return "720p";
            }
        }

        /// <summary>FFmpeg "WxH" scale string for the given resolution.</summary>
        public static string ScaleArg(Resolution r) => Width(r) + ":" + Height(r);

        public Resolution resolution  = Resolution.P720;
        public int        bitrateKbps = 2000;
        public int        fps         = 30;
        public int        gopSeconds  = 2;

        /// <summary>
        /// Extra rotation applied to the preview and the encoded stream.
        /// 0/90/180/270 clockwise. (ATAK exposes an "Auto" option driven by the
        /// device IMU; a desktop has no orientation sensor, so 0 = upright.)
        /// </summary>
        public int rotationDegrees = 0;

        /// <summary>
        /// FFmpeg video-filter fragment implementing the rotation, or null when none.
        /// Composed ahead of the scale filter by the streaming service.
        /// </summary>
        public string RotationFilter()
        {
            switch (((rotationDegrees % 360) + 360) % 360)
            {
                case 90:  return "transpose=1";                 // 90° clockwise
                case 180: return "transpose=2,transpose=2";     // 180°
                case 270: return "transpose=2";                 // 90° counter-clockwise
                default:  return null;                          // 0° — no rotation
            }
        }
    }
}
