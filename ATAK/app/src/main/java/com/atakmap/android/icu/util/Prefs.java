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
        SharedPreferences sp = ctx.getSharedPreferences(FILE, Context.MODE_PRIVATE);

        srv.destination = "SERVER".equals(sp.getString("destination", "LAN"))
                ? MediaServerConfig.Destination.SERVER : MediaServerConfig.Destination.LAN;
        srv.alias      = sp.getString("alias", "VIDEO_1");
        srv.host       = sp.getString("server_host", "");
        srv.streamPath = sp.getString("stream_path", "icu");
        srv.username   = sp.getString("username", "");
        srv.password   = sp.getString("password", "");
        srv.serverPort = sp.getInt("server_port", 1935);
        try { srv.pushProtocol = MediaServerConfig.PushProtocol.valueOf(
                sp.getString("push_protocol", "RTMP")); }
        catch (Exception ignored) { srv.pushProtocol = MediaServerConfig.PushProtocol.RTMP; }

        String res = sp.getString("resolution", "P720");
        try { enc.resolution = EncoderConfig.Resolution.valueOf(res); }
        catch (Exception ignored) { enc.resolution = EncoderConfig.Resolution.P720; }
        enc.fps             = sp.getInt("fps", 30);
        enc.bitrateKbps     = sp.getInt("bitrate", 2000);
        enc.useFrontCamera  = sp.getBoolean("front_camera", false);
        enc.rotationDegrees = sp.getInt("rotation", -1);
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
}
