using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ICUVideoStreamer.Services
{
    // Encodes MISB ST 0601.17 Local Data Set packets matching the 16 confirmed tags
    // sent by the TAK ICU Android app (PAR Government Systems Gv2F SDK).
    //
    // Pipe is read by FFmpeg with:  -f data -i \\.\pipe\<PipeName>
    // Output MPEG-TS carries it as stream type 0x06 (STANAG 4609 / SMPTE 336M).
    //
    // Tag reference (MISB ST 0601.17):
    //   1  Checksum                   uint16   CRC-16/CCITT (auto)
    //   2  Precision Timestamp        uint64   µs since Unix epoch
    //   5  Platform Heading Angle     uint16   0–360°  → 0–65535
    //   6  Platform Pitch Angle       int16    −20–+20° → −32768–32767
    //   7  Platform Roll Angle        int16    −50–+50° → −32768–32767
    //   9  Image Source Sensor        UTF-8    e.g. "WinTAK-ICU"
    //  10  Image Coordinate System    UTF-8    "Geodetic WGS84"
    //  13  Sensor Latitude            int32    ±90°   → ±2 147 483 647
    //  14  Sensor Longitude           int32    ±180°  → ±2 147 483 647
    //  15  Sensor True Altitude       uint16   −900–+19 000 m → 0–65535
    //  16  Sensor Horizontal FOV      uint16   0–180° → 0–65535
    //  17  Sensor Vertical FOV        uint16   0–180° → 0–65535
    //  18  Sensor Relative Azimuth    uint32   0–360° → 0–4 294 967 295
    //  19  Sensor Relative Elevation  int32    ±180°  → ±2 147 483 647
    //  20  Sensor Relative Roll       uint32   0–360° → 0–4 294 967 295
    //  65  UAS LDS Version Number     uint16   17 (ST 0601 rev 17)
    public sealed class KlvService : IDisposable
    {
        // ── Public pipe name ──────────────────────────────────────────────────────
        public string PipeName { get; } =
            "klv_vs_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // ── MISB ST 0601.17 Universal Label ───────────────────────────────────────
        private static readonly byte[] ST0601Key = {
            0x06, 0x0E, 0x2B, 0x34, 0x02, 0x0B, 0x01, 0x01,
            0x0E, 0x01, 0x03, 0x01, 0x01, 0x00, 0x00, 0x00
        };

        // ── Sensor state (thread-safe via _lock) ──────────────────────────────────
        private readonly object _lock = new object();
        private double _lat, _lon, _alt;
        private double _heading;    // Tag 5  – platform heading (slider)
        private double _elevation;  // Tag 19 – sensor relative elevation (slider)
        private double _hfov = 60.0;
        private double _vfov = 34.0;
        private string _sensorName = "WinTAK-ICU";

        // ── Pipe lifecycle ────────────────────────────────────────────────────────
        private Thread _thread;
        private volatile bool _running;
        private NamedPipeServerStream _activeServer;
        private bool _disposed;

        // ── Public update API ─────────────────────────────────────────────────────

        public void UpdateLocation(double lat, double lon, double alt)
        {
            lock (_lock) { _lat = lat; _lon = lon; _alt = alt; }
        }

        public void UpdateHeading(double heading)
        {
            lock (_lock) { _heading = heading; }
        }

        public void UpdateElevation(double elevation)
        {
            lock (_lock) { _elevation = elevation; }
        }

        public void UpdateFov(double hfov, double vfov)
        {
            lock (_lock) { _hfov = hfov; _vfov = vfov; }
        }

        public string SensorName
        {
            get { lock (_lock) return _sensorName; }
            set { if (!string.IsNullOrEmpty(value)) lock (_lock) _sensorName = value; }
        }

        // ── Start / Stop ──────────────────────────────────────────────────────────

        public void Start()
        {
            if (_running) return;
            TsWriter.Reset(); // fresh continuity counters for each stream
            _running = true;
            _thread = new Thread(PipeLoop) { IsBackground = true, Name = "KlvPipe" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _activeServer?.Dispose(); } catch { }
            _thread?.Join(2000);
            _thread = null;
        }

        private void PipeLoop()
        {
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName, PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    _activeServer = server;

                    // Wait up to 15 s for FFmpeg to connect, then loop
                    var connectTask = server.WaitForConnectionAsync();
                    if (!connectTask.Wait(15_000) || !_running)
                        continue;

                    while (_running && server.IsConnected)
                    {
                        double lat, lon, alt, hdg, elev, hfov, vfov;
                        string name;
                        lock (_lock)
                        {
                            lat  = _lat;  lon  = _lon;  alt   = _alt;
                            hdg  = _heading; elev = _elevation;
                            hfov = _hfov; vfov = _vfov; name  = _sensorName;
                        }

                        // Wrap raw MISB KLV in minimal MPEG-TS so FFmpeg reads it
                        // as AV_CODEC_ID_SMPTE_KLV (stream_type 0x06) via the KLVA descriptor.
                        byte[] klv = BuildPacket(lat, lon, alt, hdg, elev, hfov, vfov, name);
                        byte[] ts  = TsWriter.Wrap(klv);
                        try { server.Write(ts, 0, ts.Length); server.Flush(); }
                        catch { break; }

                        Thread.Sleep(1000); // 1 Hz — matches ICU's update rate
                    }
                }
                catch { Thread.Sleep(1000); }
                finally { _activeServer = null; server?.Dispose(); }
            }
        }

        // ── MISB ST 0601 packet builder ───────────────────────────────────────────

        private static byte[] BuildPacket(double lat, double lon, double alt,
            double heading, double elevation,
            double hfov, double vfov, string sensorName)
        {
            using (var ms = new MemoryStream())
            {
                // Tag 2: Precision Timestamp — uint64, µs since Unix epoch
                ulong us = (ulong)((DateTime.UtcNow -
                    new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .TotalMilliseconds * 1000.0);
                Item(ms, 2, BE8(us));

                // Tag 5: Platform Heading Angle — uint16, 0–360° → 0–65535
                ushort hdgEnc = (ushort)Math.Round(
                    Clamp(heading, 0, 360) / 360.0 * 65535.0);
                Item(ms, 5, BE2(hdgEnc));

                // Tag 6: Platform Pitch Angle — int16, −20 to +20° → −32768–32767
                short pitchEnc = ClampS16(0.0 / 20.0 * 32767.0); // PC has no pitch sensor → 0
                Item(ms, 6, BE2S(pitchEnc));

                // Tag 7: Platform Roll Angle — int16, −50 to +50° → −32768–32767
                short rollEnc = ClampS16(0.0 / 50.0 * 32767.0);  // PC has no roll sensor → 0
                Item(ms, 7, BE2S(rollEnc));

                // Tag 9: Image Source Sensor — UTF-8 string (e.g. "WinTAK-ICU")
                byte[] nameBytes = Encoding.UTF8.GetBytes(sensorName ?? "WinTAK-ICU");
                Item(ms, 9, nameBytes);

                // Tag 10: Image Coordinate System — UTF-8 string
                Item(ms, 10, Encoding.UTF8.GetBytes("Geodetic WGS84"));

                // Tag 13: Sensor Latitude — int32, ±90° → ±2 147 483 647
                Item(ms, 13, BE4((int)Math.Round(
                    Clamp(lat, -90, 90) / 90.0 * 2_147_483_647.0)));

                // Tag 14: Sensor Longitude — int32, ±180° → ±2 147 483 647
                Item(ms, 14, BE4((int)Math.Round(
                    Clamp(lon, -180, 180) / 180.0 * 2_147_483_647.0)));

                // Tag 15: Sensor True Altitude — uint16, −900..+19 000 m → 0–65535
                Item(ms, 15, BE2((ushort)Math.Round(
                    (Clamp(alt, -900, 19000) + 900.0) / 19900.0 * 65535.0)));

                // Tag 16: Sensor Horizontal FOV — uint16, 0–180° → 0–65535
                Item(ms, 16, BE2((ushort)Math.Round(
                    Clamp(hfov, 0, 180) / 180.0 * 65535.0)));

                // Tag 17: Sensor Vertical FOV — uint16, 0–180° → 0–65535
                Item(ms, 17, BE2((ushort)Math.Round(
                    Clamp(vfov, 0, 180) / 180.0 * 65535.0)));

                // Tag 18: Sensor Relative Azimuth — uint32, 0–360° → 0–4 294 967 295
                // Camera is bore-sighted with the platform → azimuth offset = 0
                Item(ms, 18, BE4U(0u));

                // Tag 19: Sensor Relative Elevation — int32, ±180° → ±2 147 483 647
                Item(ms, 19, BE4((int)Math.Round(
                    Clamp(elevation, -180, 180) / 180.0 * 2_147_483_647.0)));

                // Tag 20: Sensor Relative Roll — uint32, 0–360° → 0–4 294 967 295
                Item(ms, 20, BE4U(0u));

                // Tag 65: UAS LDS Version Number — uint16, value = 17 (ST 0601 rev 17)
                Item(ms, 65, BE2(17));

                return WrapLds(ms.ToArray());
            }
        }

        // ── LDS wrapper + checksum ────────────────────────────────────────────────

        private static byte[] WrapLds(byte[] items)
        {
            int payloadLen = items.Length + 4; // items + checksum TLV (1+1+2)
            using (var ms = new MemoryStream())
            {
                ms.Write(ST0601Key, 0, 16);
                BerLen(ms, payloadLen);
                ms.Write(items, 0, items.Length);
                ms.WriteByte(0x01); ms.WriteByte(0x02); // tag 1, len 2
                ms.WriteByte(0x00); ms.WriteByte(0x00); // CRC placeholder

                byte[] pkt = ms.ToArray();
                ushort crc = Crc16(pkt, pkt.Length - 2);
                pkt[pkt.Length - 2] = (byte)(crc >> 8);
                pkt[pkt.Length - 1] = (byte)(crc & 0xFF);
                return pkt;
            }
        }

        private static void Item(Stream s, byte tag, byte[] value)
        {
            s.WriteByte(tag);
            // BER short-form length (all our values are well under 127 bytes)
            s.WriteByte((byte)value.Length);
            s.Write(value, 0, value.Length);
        }

        private static void BerLen(Stream s, int len)
        {
            if (len < 128)       { s.WriteByte((byte)len); }
            else if (len <= 255) { s.WriteByte(0x81); s.WriteByte((byte)len); }
            else                 { s.WriteByte(0x82); s.WriteByte((byte)(len >> 8)); s.WriteByte((byte)(len & 0xFF)); }
        }

        // ── CRC-16/CCITT ─────────────────────────────────────────────────────────

        private static ushort Crc16(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ 0x1021)
                        : (ushort)(crc << 1);
            }
            return crc;
        }

        // ── Big-endian encoders ───────────────────────────────────────────────────

        private static byte[] BE8(ulong v) => new byte[] {
            (byte)(v>>56),(byte)(v>>48),(byte)(v>>40),(byte)(v>>32),
            (byte)(v>>24),(byte)(v>>16),(byte)(v>> 8),(byte)(v    ) };

        private static byte[] BE4(int v)
        { uint u = (uint)v; return new byte[] { (byte)(u>>24),(byte)(u>>16),(byte)(u>>8),(byte)u }; }

        private static byte[] BE4U(uint v)
        { return new byte[] { (byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v }; }

        private static byte[] BE2(ushort v)
            => new byte[] { (byte)(v >> 8), (byte)(v & 0xFF) };

        private static byte[] BE2S(short v)
        { ushort u = (ushort)v; return new byte[] { (byte)(u >> 8), (byte)(u & 0xFF) }; }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : v > hi ? hi : v;

        private static short ClampS16(double v)
            => v > 32767 ? (short)32767 : v < -32768 ? (short)-32768 : (short)Math.Round(v);

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
