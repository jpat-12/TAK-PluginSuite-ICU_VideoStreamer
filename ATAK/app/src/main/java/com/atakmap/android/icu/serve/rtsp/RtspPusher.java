package com.atakmap.android.icu.serve.rtsp;

import android.util.Base64;
import android.util.Log;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.Arrays;
import java.util.Random;

/**
 * Minimal RTSP client that PUBLISHES H.264 to a server (ANNOUNCE → SETUP → RECORD),
 * sending RTP interleaved over the RTSP TCP connection. Works with generic
 * RTSP ingest.
 *
 * <p>Optional HTTP Basic auth. Digest auth is not implemented (fails with a clear
 * message if the server demands it). Not yet validated against a live server.</p>
 */
public class RtspPusher {

    private static final String TAG = "ICU.RtspPush";

    private final String host;
    private final int    port;
    private final String path;
    private final String user, pass;

    private final String baseUrl;
    private Socket socket;
    private InputStream inRaw;
    private BufferedReader in;
    private OutputStream out;
    private int cseq = 1;
    private String session;
    private String authHeader; // cached Basic auth once known to be needed

    private final int ssrc = new Random().nextInt();
    private int seq = 0;
    private byte[] sps, pps;

    public RtspPusher(String host, int port, String path, String user, String pass) {
        this.host = host;
        this.port = port;
        this.path = path.startsWith("/") ? path.substring(1) : path;
        this.user = user;
        this.pass = pass;
        this.baseUrl = "rtsp://" + host + ":" + port + "/" + this.path;
    }

    // ── Publish handshake ────────────────────────────────────────────────────

    public void publish(byte[] sps, byte[] pps) throws IOException {
        this.sps = strip(sps);
        this.pps = strip(pps);

        socket = new Socket();
        socket.connect(new InetSocketAddress(host, port), 5000);
        socket.setTcpNoDelay(true);
        inRaw = socket.getInputStream();
        in    = new BufferedReader(new InputStreamReader(inRaw));
        out   = socket.getOutputStream();

        request("OPTIONS", baseUrl, null, null);

        String sdp = buildSdp();
        Resp announce = request("ANNOUNCE", baseUrl,
                "Content-Type: application/sdp\r\nContent-Length: " + sdp.length() + "\r\n", sdp);
        if (announce.code == 401) {
            // retry once with Basic auth if we have credentials
            if (user != null && !user.isEmpty()) {
                authHeader = "Authorization: Basic " + Base64.encodeToString(
                        (user + ":" + pass).getBytes("UTF-8"), Base64.NO_WRAP) + "\r\n";
                announce = request("ANNOUNCE", baseUrl,
                        "Content-Type: application/sdp\r\nContent-Length: " + sdp.length() + "\r\n", sdp);
            }
            if (announce.code == 401)
                throw new IOException(announce.headers.contains("Digest")
                        ? "server requires Digest auth (not supported)" : "authentication failed");
        }
        if (announce.code != 200) throw new IOException("ANNOUNCE failed: " + announce.code);

        Resp setup = request("SETUP", baseUrl + "/trackID=0",
                "Transport: RTP/AVP/TCP;unicast;interleaved=0-1;mode=record\r\n", null);
        if (setup.code != 200) throw new IOException("SETUP failed: " + setup.code);
        session = parseSession(setup.headers);

        Resp record = request("RECORD", baseUrl, "Range: npt=0.000-\r\n", null);
        if (record.code != 200) throw new IOException("RECORD failed: " + record.code);
        Log.d(TAG, "RTSP publishing → " + baseUrl + " (session " + session + ")");
    }

    public void close() {
        try { if (session != null) request("TEARDOWN", baseUrl, null, null); } catch (Exception ignored) {}
        try { if (socket != null) socket.close(); } catch (Exception ignored) {}
        socket = null;
    }

    // ── RTP send (interleaved over TCP) ───────────────────────────────────────

    public void sendNal(byte[] annexB, boolean keyFrame, long ptsUs) {
        if (out == null) return;
        long ts = ptsUs * 90L / 1000L;
        byte[] nal = strip(annexB);
        try {
            if (keyFrame) {
                if (sps != null) sendRtp(sps, false, ts);
                if (pps != null) sendRtp(pps, false, ts);
            }
            sendRtp(nal, true, ts);
        } catch (IOException e) {
            Log.w(TAG, "sendNal: " + e.getMessage());
        }
    }

    private void sendRtp(byte[] nal, boolean marker, long ts) throws IOException {
        if (nal.length <= 1400) {
            writeInterleaved(buildRtp(nal, 0, nal.length, marker, ts));
        } else {
            // FU-A fragmentation
            byte nalHeader = nal[0];
            byte fuInd = (byte) ((nalHeader & 0xE0) | 28);
            int pos = 1;
            boolean first = true;
            while (pos < nal.length) {
                int chunk = Math.min(nal.length - pos, 1398);
                boolean last = (pos + chunk >= nal.length);
                byte fuHdr = (byte) (nalHeader & 0x1F);
                if (first) fuHdr |= 0x80;
                if (last)  fuHdr |= 0x40;
                byte[] payload = new byte[2 + chunk];
                payload[0] = fuInd; payload[1] = fuHdr;
                System.arraycopy(nal, pos, payload, 2, chunk);
                writeInterleaved(buildRtp(payload, 0, payload.length, last && marker, ts));
                first = false; pos += chunk;
            }
        }
    }

    private byte[] buildRtp(byte[] d, int off, int len, boolean marker, long ts) {
        byte[] p = new byte[12 + len];
        int s = seq++ & 0xFFFF;
        p[0] = (byte) 0x80;
        p[1] = (byte) ((marker ? 0x80 : 0) | 96);
        p[2] = (byte) (s >> 8); p[3] = (byte) s;
        p[4] = (byte) (ts >> 24); p[5] = (byte) (ts >> 16); p[6] = (byte) (ts >> 8); p[7] = (byte) ts;
        p[8] = (byte) (ssrc >> 24); p[9] = (byte) (ssrc >> 16); p[10] = (byte) (ssrc >> 8); p[11] = (byte) ssrc;
        System.arraycopy(d, off, p, 12, len);
        return p;
    }

    private synchronized void writeInterleaved(byte[] rtp) throws IOException {
        out.write(0x24);          // '$'
        out.write(0);             // channel 0 (RTP)
        out.write((rtp.length >> 8) & 0xFF);
        out.write(rtp.length & 0xFF);
        out.write(rtp);
        out.flush();
    }

    // ── RTSP request/response ─────────────────────────────────────────────────

    private static final class Resp { int code; String headers = ""; }

    private synchronized Resp request(String method, String url, String extraHeaders, String body)
            throws IOException {
        StringBuilder r = new StringBuilder();
        r.append(method).append(' ').append(url).append(" RTSP/1.0\r\n");
        r.append("CSeq: ").append(cseq++).append("\r\n");
        r.append("User-Agent: ICU-VideoStreamer\r\n");
        if (session != null) r.append("Session: ").append(session).append("\r\n");
        if (authHeader != null) r.append(authHeader);
        if (extraHeaders != null) r.append(extraHeaders);
        r.append("\r\n");
        if (body != null) r.append(body);
        out.write(r.toString().getBytes("UTF-8"));
        out.flush();
        return readResponse();
    }

    private Resp readResponse() throws IOException {
        Resp resp = new Resp();
        String status = in.readLine();
        if (status == null) throw new IOException("connection closed");
        // "RTSP/1.0 200 OK"
        String[] parts = status.split(" ");
        resp.code = (parts.length >= 2) ? parseInt(parts[1]) : 0;
        StringBuilder h = new StringBuilder();
        String line; int contentLength = 0;
        while ((line = in.readLine()) != null && !line.isEmpty()) {
            h.append(line).append("\r\n");
            if (line.toLowerCase().startsWith("content-length:"))
                contentLength = parseInt(line.substring(15).trim());
        }
        resp.headers = h.toString();
        for (int i = 0; i < contentLength; i++) in.read(); // drain any body
        return resp;
    }

    private static String parseSession(String headers) {
        for (String line : headers.split("\r\n")) {
            if (line.toLowerCase().startsWith("session:")) {
                String v = line.substring(8).trim();
                int semi = v.indexOf(';');
                return semi >= 0 ? v.substring(0, semi).trim() : v;
            }
        }
        return null;
    }

    private String buildSdp() {
        String spsB64 = Base64.encodeToString(sps, Base64.NO_WRAP);
        String ppsB64 = Base64.encodeToString(pps, Base64.NO_WRAP);
        return "v=0\r\n" +
               "o=- 0 0 IN IP4 127.0.0.1\r\n" +
               "s=ICU VideoStreamer\r\n" +
               "c=IN IP4 0.0.0.0\r\n" +
               "t=0 0\r\n" +
               "m=video 0 RTP/AVP 96\r\n" +
               "a=rtpmap:96 H264/90000\r\n" +
               "a=fmtp:96 packetization-mode=1;sprop-parameter-sets=" + spsB64 + "," + ppsB64 + "\r\n" +
               "a=control:trackID=0\r\n";
    }

    private static byte[] strip(byte[] d) {
        if (d == null) return null;
        int off = 0;
        if (d.length > 4 && d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 1) off = 4;
        else if (d.length > 3 && d[0] == 0 && d[1] == 0 && d[2] == 1) off = 3;
        return off == 0 ? d : Arrays.copyOfRange(d, off, d.length);
    }

    private static int parseInt(String s) {
        try { return Integer.parseInt(s.trim()); } catch (Exception e) { return 0; }
    }
}
