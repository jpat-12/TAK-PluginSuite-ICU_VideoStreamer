package com.atakmap.android.icu.share;

import android.os.Handler;
import android.os.Looper;

import com.atakmap.android.cot.CotMapComponent;
import com.atakmap.android.cot.detail.SensorDetailHandler;
import com.atakmap.android.maps.MapGroup;
import com.atakmap.android.maps.MapItem;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.maps.Marker;
import com.atakmap.coremap.cot.event.CotDetail;
import com.atakmap.coremap.log.Log;

import java.net.URI;
import java.util.UUID;

/**
 * While broadcasting, makes the operator's <b>own self CoT</b> carry the field-of-view and
 * the playable feed — so peers see the FOV wedge off the skittle and can tap it to watch —
 * without any separate sensor marker.
 *
 * <p>Uses ATAK's purpose-built {@link CotMapComponent#addAdditionalDetail} hook, which
 * appends a {@code <detail>} child to every outbound self SA report. This is the only
 * reliable way to inject into the self PLI (plain {@code CotDetailHandler}s are not invoked
 * for the self marker), and it never mutates the live self marker's metadata — so it can't
 * corrupt "lock on self" the way earlier attempts did.</p>
 *
 * <p>Locally we also call {@link SensorDetailHandler#addFovToMap} so the broadcaster sees
 * their own wedge (the outbound hook only affects what's transmitted). Both are refreshed on
 * a short timer so the azimuth tracks heading and the wedge survives self-marker refreshes.</p>
 */
public final class SelfMarkerFov {

    private static final String TAG = "ICU.SelfFov";

    private static final String KEY_SENSOR = "__icu_sensor";
    private static final String KEY_VIDEO  = "__icu_video";

    private static final double FOV_DEG = 60, RANGE_M = 200;
    private static final float[] FOV_RGBA = { 0.0f, 0.6f, 1.0f, 0.3f };
    private static final long REAPPLY_MS = 3000;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private volatile boolean active;
    private String url;
    private String alias = "ICU VideoStreamer";
    private String videoUid;

    public void start(String videoUrl, String alias) {
        this.url = videoUrl;
        if (alias != null && !alias.trim().isEmpty()) this.alias = alias.trim();
        this.videoUid = "ICU-" + UUID.randomUUID();
        active = true;
        handler.post(tick);
        Log.d(TAG, "self CoT FOV+video on: " + videoUrl);
    }

    public void stop() {
        if (!active) return;
        active = false;
        handler.removeCallbacks(tick);
        try {
            CotMapComponent cmc = CotMapComponent.getInstance();
            if (cmc != null) {
                cmc.removeAdditionalDetail(KEY_SENSOR);
                cmc.removeAdditionalDetail(KEY_VIDEO);
            }
        } catch (Throwable t) {
            Log.w(TAG, "remove additional detail: " + t.getMessage());
        }
        removeLocalFov();
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private final Runnable tick = new Runnable() {
        @Override public void run() {
            if (!active) return;
            apply();
            handler.postDelayed(this, REAPPLY_MS);
        }
    };

    private void apply() {
        try {
            double az = heading();
            CotMapComponent cmc = CotMapComponent.getInstance();
            if (cmc != null) {
                cmc.addAdditionalDetail(KEY_SENSOR, sensorDetail(az));   // → outbound self PLI
                cmc.addAdditionalDetail(KEY_VIDEO, videoDetail());
            }
            renderLocalFov(az);   // so the broadcaster sees the wedge on their own device
        } catch (Throwable t) {
            Log.w(TAG, "apply: " + t.getMessage());
        }
    }

    private CotDetail sensorDetail(double az) {
        CotDetail s = new CotDetail("sensor");
        s.setAttribute("fov", Long.toString(Math.round(FOV_DEG)));
        s.setAttribute("azimuth", Long.toString(Math.round(az)));
        s.setAttribute("range", Long.toString(Math.round(RANGE_M)));
        s.setAttribute("fovRed", "0.0");
        s.setAttribute("fovGreen", "0.6");
        s.setAttribute("fovBlue", "1.0");
        s.setAttribute("fovAlpha", "0.3");
        s.setAttribute("displayMagneticReference", "0");
        return s;
    }

    private CotDetail videoDetail() {
        CotDetail video = new CotDetail("__video");
        video.setAttribute("uid", videoUid);
        video.setAttribute("url", url);

        String address = "", path = "", protocol = "rtsp";
        int port = 8554;
        try {
            URI u = URI.create(url);
            if (u.getScheme() != null) protocol = u.getScheme().toLowerCase();
            if (u.getHost() != null)   address  = u.getHost();
            if (u.getPort() > 0)       port     = u.getPort();
            if (u.getPath() != null)   path     = u.getPath();
        } catch (Exception ignored) {}

        CotDetail ce = new CotDetail("ConnectionEntry");
        ce.setAttribute("uid", videoUid);
        ce.setAttribute("alias", alias);
        ce.setAttribute("address", address);
        ce.setAttribute("port", Integer.toString(port));
        ce.setAttribute("roverPort", "-1");
        ce.setAttribute("path", path);
        ce.setAttribute("protocol", protocol);
        ce.setAttribute("networkTimeout", "5000");
        ce.setAttribute("bufferTime", "-1");
        ce.setAttribute("rtspReliable", "0");
        ce.setAttribute("ignoreEmbeddedKLV", "false");
        video.addChild(ce);
        return video;
    }

    private void renderLocalFov(double az) {
        Marker self = selfMarker();
        if (self != null)
            SensorDetailHandler.addFovToMap(self, az, FOV_DEG, RANGE_M, FOV_RGBA, false);
    }

    private void removeLocalFov() {
        try {
            Marker self = selfMarker();
            if (self == null) return;
            self.removeMetaData(SensorDetailHandler.SENSOR_FOV);
            MapGroup g = SensorDetailHandler.getOrAddMapGroup();
            if (g != null) {
                MapItem fov = g.deepFindUID(self.getUID() + SensorDetailHandler.UID_POSTFIX);
                if (fov != null) fov.removeFromGroup();
            }
        } catch (Throwable t) {
            Log.w(TAG, "removeLocalFov: " + t.getMessage());
        }
    }

    private static double heading() {
        Marker self = selfMarker();
        if (self == null) return 0;
        double h = self.getTrackHeading();
        return Double.isNaN(h) ? 0 : h;
    }

    private static Marker selfMarker() {
        MapView mv = MapView.getMapView();
        return mv != null ? mv.getSelfMarker() : null;
    }
}
