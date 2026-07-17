using System;
using System.IO;

namespace ICUVideoStreamer.Klv
{
    /// <summary>
    /// Wraps raw MISB ST 0601 KLV packets in a minimal MPEG-TS byte stream.
    ///
    /// Output PID layout:
    ///   0x0000 (PAT) — program 1 → PMT at PID 0x1000
    ///   0x1000 (PMT) — program 1, KLV PES at PID 0x0064, stream_type 0x06
    ///   0x0064 (KLV) — PES private stream 1, payload = raw MISB KLV packet
    ///
    /// The 'KLVA' registration descriptor (ISO 13818-1 §2.6.8, SMPTE 336M) tells
    /// FFmpeg's mpegts demuxer to map the stream to AV_CODEC_ID_SMPTE_KLV.
    /// The output mpegts muxer then emits it with stream_type 0x06 (STANAG 4609).
    ///
    /// Usage:
    ///   TsWriter.Reset();                   // call once when stream starts
    ///   byte[] ts = TsWriter.Wrap(klvBytes); // called for each KLV packet
    /// </summary>
    internal static class TsWriter
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        private const int    TS_PAYLOAD  = 184;     // TS packet payload bytes
        private const ushort PAT_PID     = 0x0000;
        private const ushort PMT_PID     = 0x1000;  // 4096
        private const ushort KLV_PID     = 0x0064;  // 100
        private const ushort PROGRAM     = 0x0001;
        private const ushort TS_ID       = 0x0001;
        private const byte   STREAM_TYPE = 0x06;    // SMPTE 336M / STANAG 4609
        private const byte   STREAM_ID   = 0xBD;    // PES stream_id: private stream 1
        private const ushort NO_PCR      = 0x1FFF;  // PMT PCR_PID = "no PCR"

        // ── Continuity counters (4-bit, wraps at 16) ──────────────────────────────

        private static int _patCc;
        private static int _pmtCc;
        private static int _klvCc;

        /// <summary>Call once before each new stream to reset continuity counters.</summary>
        public static void Reset()
        {
            _patCc = 0;
            _pmtCc = 0;
            _klvCc = 0;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Wraps one raw MISB ST 0601 KLV packet in minimal MPEG-TS.
        /// PAT and PMT tables are injected with every call (1 Hz matches KLV rate).
        /// Returns a byte array of complete 188-byte TS packets.
        /// </summary>
        public static byte[] Wrap(byte[] klv)
        {
            using (var ms = new MemoryStream(4 * 188))
            {
                EmitSection(ms, PAT_PID, BuildPat(), ref _patCc);
                EmitSection(ms, PMT_PID, BuildPmt(), ref _pmtCc);
                EmitPes(ms, KLV_PID, BuildPes(klv), ref _klvCc);
                return ms.ToArray();
            }
        }

        // ── PAT (Program Association Table) ───────────────────────────────────────

        private static byte[] BuildPat()
        {
            // section_length = 5 (fixed header after length field)
            //                + 4 (1 program entry: program_num + PMT_PID)
            //                + 4 (CRC) = 13
            const int SECTION_LEN = 13;

            var sec = new byte[3 + SECTION_LEN - 4]; // body without CRC
            int i = 0;
            sec[i++] = 0x00;             // table_id = PAT
            sec[i++] = 0xB0;             // section_syntax=1, '0', reserved=11, len high nibble=0
            sec[i++] = SECTION_LEN;      // section_length low byte
            sec[i++] = (byte)(TS_ID >> 8);
            sec[i++] = (byte)(TS_ID & 0xFF);
            sec[i++] = 0xC1;             // reserved=11, version=0, current_next=1
            sec[i++] = 0x00;             // section_number
            sec[i++] = 0x00;             // last_section_number
            // Program 1 → PMT_PID
            sec[i++] = (byte)(PROGRAM >> 8);
            sec[i++] = (byte)(PROGRAM & 0xFF);
            sec[i++] = (byte)(0xE0 | (PMT_PID >> 8));
            sec[i++] = (byte)(PMT_PID & 0xFF);

            return AppendCrc32(sec, i);
        }

        // ── PMT (Program Map Table) ───────────────────────────────────────────────

        private static byte[] BuildPmt()
        {
            // Registration descriptor: tag=0x05, length=4, format_identifier='K','L','V','A'
            // This is the SMPTE 336M / STANAG 4609 identifier that FFmpeg recognises.
            byte[] regDesc = { 0x05, 0x04, (byte)'K', (byte)'L', (byte)'V', (byte)'A' };

            // section_length = 9  (program_num(2)+version(1)+sect_num(1)+last(1)+PCR_PID(2)+prog_info_len(2))
            //                + 5  (stream_type(1)+elem_PID(2)+ES_info_len(2))
            //                + 6  (regDesc.Length)
            //                + 4  (CRC)
            //                = 24
            int sectionLen = 9 + 5 + regDesc.Length + 4;

            var sec = new byte[3 + sectionLen - 4]; // body without CRC
            int i = 0;
            sec[i++] = 0x02;                              // table_id = PMT
            sec[i++] = (byte)(0xB0 | (sectionLen >> 8));  // section_syntax=1, reserved, len high
            sec[i++] = (byte)(sectionLen & 0xFF);         // section_length low byte
            sec[i++] = (byte)(PROGRAM >> 8);
            sec[i++] = (byte)(PROGRAM & 0xFF);
            sec[i++] = 0xC1;                              // reserved=11, version=0, current_next=1
            sec[i++] = 0x00;                              // section_number
            sec[i++] = 0x00;                              // last_section_number
            // PCR_PID = 0x1FFF (no PCR in this data-only sub-stream)
            sec[i++] = (byte)(0xE0 | (NO_PCR >> 8));
            sec[i++] = (byte)(NO_PCR & 0xFF);
            sec[i++] = 0xF0;                              // reserved=1111, prog_info_length high=0
            sec[i++] = 0x00;                              // prog_info_length = 0
            // Elementary stream: KLV
            sec[i++] = STREAM_TYPE;                       // 0x06
            sec[i++] = (byte)(0xE0 | (KLV_PID >> 8));
            sec[i++] = (byte)(KLV_PID & 0xFF);
            sec[i++] = (byte)(0xF0 | (regDesc.Length >> 8));
            sec[i++] = (byte)(regDesc.Length & 0xFF);     // ES_info_length = 6
            Array.Copy(regDesc, 0, sec, i, regDesc.Length);
            i += regDesc.Length;

            return AppendCrc32(sec, i);
        }

        // ── PES (Packetised Elementary Stream) wrapper ────────────────────────────

        private static byte[] BuildPes(byte[] klv)
        {
            // PES_packet_length = optional_header_bytes(3) + payload_bytes(klv.Length)
            int pesPayloadLen = 3 + klv.Length;

            var pes = new byte[6 + 3 + klv.Length]; // 6-byte fixed PES header + 3 optional + data
            int i = 0;
            // Packet start code prefix
            pes[i++] = 0x00;
            pes[i++] = 0x00;
            pes[i++] = 0x01;
            pes[i++] = STREAM_ID;                            // 0xBD private stream 1
            pes[i++] = (byte)(pesPayloadLen >> 8);
            pes[i++] = (byte)(pesPayloadLen & 0xFF);
            // Flags:
            //   byte1: '10' (required) | scrambling=00 | priority=0 | data_align=1 | copyright=0 | orig=0 = 0x84
            //   byte2: PTS_DTS=00 | all others=0 = 0x00
            //   byte3: PES_header_data_length = 0 (no PTS/DTS)
            pes[i++] = 0x84;
            pes[i++] = 0x00;
            pes[i++] = 0x00;
            Array.Copy(klv, 0, pes, i, klv.Length);
            return pes;
        }

        // ── TS emission ───────────────────────────────────────────────────────────

        /// <summary>
        /// Emits a PSI section (PAT/PMT) as exactly one TS packet with PUSI=1.
        /// Section data must be ≤ 183 bytes (184 - 1 pointer_field byte).
        /// Remainder is stuffed with 0xFF.
        /// </summary>
        private static void EmitSection(MemoryStream ms, ushort pid, byte[] section, ref int cc)
        {
            var payload = new byte[TS_PAYLOAD];
            payload[0] = 0x00; // pointer_field: section starts immediately
            int copy = Math.Min(section.Length, TS_PAYLOAD - 1);
            Array.Copy(section, 0, payload, 1, copy);
            for (int k = 1 + copy; k < TS_PAYLOAD; k++)
                payload[k] = 0xFF; // stuffing bytes
            WriteTsPacket(ms, pid, pusi: true, payload, ref cc);
        }

        /// <summary>
        /// Emits an arbitrary payload split across as many TS packets as needed.
        /// PUSI is set on the first packet only.
        /// Trailing partial packets are stuffed with 0xFF.
        /// </summary>
        private static void EmitPes(MemoryStream ms, ushort pid, byte[] data, ref int cc)
        {
            int offset = 0;
            bool first = true;
            while (offset < data.Length)
            {
                var payload = new byte[TS_PAYLOAD];
                int remaining = data.Length - offset;
                int copy = Math.Min(remaining, TS_PAYLOAD);
                Array.Copy(data, offset, payload, 0, copy);
                if (copy < TS_PAYLOAD)
                    for (int k = copy; k < TS_PAYLOAD; k++)
                        payload[k] = 0xFF;
                WriteTsPacket(ms, pid, first, payload, ref cc);
                offset += copy;
                first = false;
            }
        }

        private static void WriteTsPacket(MemoryStream ms, ushort pid, bool pusi, byte[] payload, ref int cc)
        {
            ms.WriteByte(0x47); // sync byte
            ms.WriteByte((byte)((pusi ? 0x40 : 0x00) | ((pid >> 8) & 0x1F)));
            ms.WriteByte((byte)(pid & 0xFF));
            ms.WriteByte((byte)(0x10 | (cc & 0x0F))); // adaptation_field_control=01 (payload only)
            ms.Write(payload, 0, TS_PAYLOAD);
            cc = (cc + 1) & 0x0F;
        }

        // ── CRC helpers ───────────────────────────────────────────────────────────

        private static byte[] AppendCrc32(byte[] data, int bodyLen)
        {
            uint crc = ComputeCrc32(data, 0, bodyLen);
            var result = new byte[bodyLen + 4];
            Array.Copy(data, result, bodyLen);
            result[bodyLen + 0] = (byte)(crc >> 24);
            result[bodyLen + 1] = (byte)(crc >> 16);
            result[bodyLen + 2] = (byte)(crc >> 8);
            result[bodyLen + 3] = (byte)(crc & 0xFF);
            return result;
        }

        // CRC-32/MPEG-2: poly=0x04C11DB7, init=0xFFFFFFFF, no reflection, no final XOR
        private static readonly uint[] _crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            const uint POLY = 0x04C11DB7;
            var t = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                uint crc = (uint)i << 24;
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ POLY : crc << 1;
                t[i] = crc;
            }
            return t;
        }

        private static uint ComputeCrc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + length; i++)
                crc = (_crcTable[((crc >> 24) ^ data[i]) & 0xFF]) ^ (crc << 8);
            return crc;
        }
    }
}
