package com.atakmap.android.icu.util;

import android.content.Context;
import android.content.SharedPreferences;

import com.atakmap.android.icu.capture.EncoderConfig;
import com.atakmap.android.icu.serve.MediaServerConfig;

/**
 * Persistence for ICU broadcast settings (destination, server, credentials, encoding).
 *
 * <p>IMPORTANT: pass the <b>host ATAK context</b> ({@code getMapView().getContext()}),
 * NOT the plugin context. A plugin context's SharedPreferences are not backed by
 * ATAK's persistent data directory, so they do not survive an ATAK restart — the
 * values only live in the in-memory cache for the current session. Using the host
 * context writes to ATAK's own {@code shared_prefs} dir, which persists.</p>
 */
public final class Prefs {

    private static final String FILE = "icu_video_prefs";

    private Prefs() {}

    public static void load(Context ctx, MediaServerConfig srv, EncoderConfig enc) {
        SharedPreferences sp = ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE);

        srv.destination = "LAN".equals(sp.getString("destination", "SERVER"))
                ? MediaServerConfig.Destination.LAN : MediaServerConfig.Destination.SERVER;
        srv.alias      = sp.getString("alias", "VIDEO_1");
        srv.host       = sp.getString("server_host", "");
        srv.streamPath = sp.getString("stream_path", "icu");
        srv.username   = sp.getString("username", "");
        srv.password   = sp.getString("password", "");
        srv.serverPort = sp.getInt("server_port", 8554);
        srv.srtPassphrase = sp.getString("srt_passphrase", "");
        try { srv.pushProtocol = MediaServerConfig.PushProtocol.valueOf(
                sp.getString("push_protocol", "RTSP")); }
        catch (Exception ignored) { srv.pushProtocol = MediaServerConfig.PushProtocol.RTSP; }

        String res = sp.getString("resolution", "P720");
        try { enc.resolution = EncoderConfig.Resolution.valueOf(res); }
        catch (Exception ignored) { enc.resolution = EncoderConfig.Resolution.P720; }
        enc.fps             = sp.getInt("fps", 30);
        enc.bitrateKbps     = sp.getInt("bitrate", 2000);
        enc.useFrontCamera  = sp.getBoolean("front_camera", false);
        enc.rotationDegrees = sp.getInt("rotation", 270);
        enc.showStatusWidget = sp.getBoolean("show_status_widget", true);
        enc.streamWithScreenOff = sp.getBoolean("stream_screen_off", false);
    }

    public static void save(Context ctx, MediaServerConfig srv, EncoderConfig enc) {
        // commit() (not apply()) so the write is on disk before this call returns —
        // apply()'s write-behind can otherwise be lost if ATAK is force-stopped/killed
        // shortly after Save, which is exactly what "restart ATAK" testing does.
        ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit()
                .putString("destination", srv.destination.name())
                .putString("alias", nz(srv.alias, "VIDEO_1"))
                .putString("server_host", srv.host == null ? "" : srv.host.trim())
                .putString("stream_path", nz(srv.streamPath, "icu"))
                .putString("username", srv.username == null ? "" : srv.username)
                .putString("password", srv.password == null ? "" : srv.password)
                .putInt("server_port", srv.serverPort)
                .putString("srt_passphrase", srv.srtPassphrase == null ? "" : srv.srtPassphrase)
                .putString("push_protocol", srv.pushProtocol.name())
                .putString("resolution", enc.resolution.name())
                .putInt("fps", enc.fps)
                .putInt("bitrate", enc.bitrateKbps)
                .putBoolean("front_camera", enc.useFrontCamera)
                .putInt("rotation", enc.rotationDegrees)
                .putBoolean("show_status_widget", enc.showStatusWidget)
                .putBoolean("stream_screen_off", enc.streamWithScreenOff)
                .commit();
    }

    private static String nz(String v, String def) {
        return (v == null || v.trim().isEmpty()) ? def : v.trim();
    }
}
