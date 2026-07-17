package com.atakmap.android.icu.util;

import android.content.Context;
import android.content.SharedPreferences;

import com.atakmap.android.icu.capture.EncoderConfig;
import com.atakmap.android.icu.serve.MediaServerConfig;

/**
 * Persistence for ICU broadcast settings (destination, server, credentials, encoding).
 * Stored against the plugin context so it survives ATAK restarts.
 */
public final class Prefs {

    private static final String FILE = "icu_video_prefs";

    private Prefs() {}

    public static void load(Context ctx, MediaServerConfig srv, EncoderConfig enc) {
        load(ctx, srv, enc, null);
    }

    /**
     * @param defaultStreamPath used as the stream path / name the first time the plugin
     *        runs (before the user has ever set one) — pass the device callsign so each
     *        operator's stream lands on a distinct path and streams don't collide.
     */
    public static void load(Context ctx, MediaServerConfig srv, EncoderConfig enc,
                            String defaultStreamPath) {
        SharedPreferences sp = ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE);

        srv.destination = "LAN".equals(sp.getString("destination", "SERVER"))
                ? MediaServerConfig.Destination.LAN : MediaServerConfig.Destination.SERVER;
        srv.alias      = sp.getString("alias", "VIDEO_1");
        srv.host       = sp.getString("server_host", "");
        // First run: seed the path from the callsign; afterwards honor the saved value.
        String seedPath = sanitizePathSegment(defaultStreamPath, "icu");
        srv.streamPath = sp.contains("stream_path")
                ? sp.getString("stream_path", seedPath) : seedPath;
        srv.username   = sp.getString("username", "");
        srv.password   = sp.getString("password", "");
        srv.serverPort = sp.getInt("server_port", 8554);
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
    }

    public static void save(Context ctx, MediaServerConfig srv, EncoderConfig enc) {
        ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit()
                .putString("destination", srv.destination.name())
                .putString("alias", nz(srv.alias, "VIDEO_1"))
                .putString("server_host", srv.host == null ? "" : srv.host.trim())
                .putString("stream_path", nz(srv.streamPath, "icu"))
                .putString("username", srv.username == null ? "" : srv.username)
                .putString("password", srv.password == null ? "" : srv.password)
                .putInt("server_port", srv.serverPort)
                .putString("push_protocol", srv.pushProtocol.name())
                .putString("resolution", enc.resolution.name())
                .putInt("fps", enc.fps)
                .putInt("bitrate", enc.bitrateKbps)
                .putBoolean("front_camera", enc.useFrontCamera)
                .putInt("rotation", enc.rotationDegrees)
                .apply();
    }

    private static String nz(String v, String def) {
        return (v == null || v.trim().isEmpty()) ? def : v.trim();
    }

    /**
     * Make a callsign safe to use as a URL/stream path segment: trim, collapse spaces to
     * underscores, and drop anything outside [A-Za-z0-9._-]. Falls back to {@code def}
     * when the result would be empty.
     */
    static String sanitizePathSegment(String v, String def) {
        if (v == null) return def;
        String s = v.trim().replaceAll("\\s+", "_").replaceAll("[^A-Za-z0-9._-]", "");
        return s.isEmpty() ? def : s;
    }
}
