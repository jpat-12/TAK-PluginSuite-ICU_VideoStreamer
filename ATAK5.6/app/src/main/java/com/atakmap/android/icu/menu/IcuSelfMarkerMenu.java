package com.atakmap.android.icu.menu;

import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.util.Base64;

import com.atakmap.android.icu.ICUVideoDropDownReceiver;
import com.atakmap.android.icu.plugin.R;
import com.atakmap.android.ipc.AtakBroadcast;
import com.atakmap.android.maps.MapDataRef;
import com.atakmap.android.maps.MapItem;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.menu.MapMenuButtonWidget;
import com.atakmap.android.menu.MapMenuHandler;
import com.atakmap.android.menu.MapMenuReceiver;
import com.atakmap.android.menu.MapMenuWidget;
import com.atakmap.android.widgets.AbstractButtonWidget;
import com.atakmap.android.widgets.WidgetIcon;
import com.atakmap.coremap.log.Log;

import java.io.ByteArrayOutputStream;

/**
 * Part B2 — adds a dedicated <b>ICU</b> entry to the operator's own self-marker radial
 * menu, opening a submenu of Broadcast / Record / Snapshot. Each button fires the
 * matching headless intent handled by {@link ICUVideoDropDownReceiver} (so the map
 * radial drives the same actions as the panel, panel open or not).
 *
 * <p>Registered as a {@link MapMenuHandler} (not a {@code MapMenuFactory}) on purpose:
 * {@link #updateMenu} is handed the <i>already-built</i> menu, so we <b>append</b> our
 * button and never clobber ATAK's default self-marker options.</p>
 *
 * <p>Radial buttons are icon-based — the GL renderer dereferences the button's
 * {@link WidgetIcon}, so a button with no icon crashes ATAK. Our drawables are vectors,
 * which {@code android.resource://} can't decode as a bitmap, so we rasterize each one
 * and embed it as a {@code base64://} ref (no filesystem — the plugin cache dir isn't
 * writable anyway).</p>
 */
public final class IcuSelfMarkerMenu implements MapMenuHandler {

    private static final String TAG = "ICU.RadialMenu";
    private static final int ICON_PX = 48;   // rasterized radial icon size

    /** ATAK context used to construct the radial widgets. */
    private final Context ctx;
    /** Plugin context — resolves the plugin's own drawable resources. */
    private final Context pluginCtx;
    private boolean registered;

    public IcuSelfMarkerMenu(Context ctx, Context pluginCtx) {
        this.ctx = ctx;
        this.pluginCtx = pluginCtx;
    }

    public void register() {
        MapMenuReceiver r = MapMenuReceiver.getInstance();
        if (r != null && !registered) {
            r.registerMapMenuHandler(this);
            registered = true;
            Log.d(TAG, "ICU self-marker radial handler registered");
        }
    }

    public void dispose() {
        MapMenuReceiver r = MapMenuReceiver.getInstance();
        if (r != null && registered) {
            r.unregisterMapMenuHandler(this);
            registered = false;
        }
    }

    @Override
    public void updateMenu(MapItem item, MapMenuWidget menu) {
        MapView mv = MapView.getMapView();
        if (mv == null || menu == null || item == null || item != mv.getSelfMarker())
            return;

        MapMenuButtonWidget icu = button(R.drawable.ic_icu_menu, null);
        if (icu == null) return;   // couldn't build the icon → skip rather than crash

        MapMenuWidget sub = new MapMenuWidget();
        addButton(sub, R.drawable.ic_broadcast, ICUVideoDropDownReceiver.TOGGLE);
        addButton(sub, R.drawable.ic_record,    ICUVideoDropDownReceiver.RECORD);
        addButton(sub, R.drawable.ic_snapshot,  ICUVideoDropDownReceiver.SNAPSHOT);
        icu.setSubmenu(sub);

        menu.addWidget(icu);   // append, keeping ATAK's default self-marker buttons
    }

    private void addButton(MapMenuWidget parent, int iconRes, String action) {
        MapMenuButtonWidget b = button(iconRes, action);
        if (b != null) parent.addWidget(b);
    }

    /**
     * Build a radial button with a rasterized icon. A non-null {@code action} broadcasts
     * that intent on click (handled by {@link ICUVideoDropDownReceiver}); a null action
     * is a parent that only opens its submenu. Returns null if the icon can't be built
     * (a radial button without an icon would crash the GL renderer).
     */
    private MapMenuButtonWidget button(int iconRes, final String action) {
        WidgetIcon icon = icon(iconRes);
        if (icon == null) return null;
        MapMenuButtonWidget b = new MapMenuButtonWidget(ctx);
        b.setIcon(icon);
        if (action != null) {
            b.setOnButtonClickHandler(new gov.tak.api.widgets.IMapMenuButtonWidget.OnButtonClickHandler() {
                @Override public boolean isSupported(Object o) { return true; }
                @Override public void performAction(Object o) {
                    AtakBroadcast.getInstance().sendBroadcast(new Intent(action));
                }
            });
        }
        return b;
    }

    /** Rasterize a (possibly vector) plugin drawable into a base64-backed WidgetIcon. */
    private WidgetIcon icon(int iconRes) {
        try {
            Drawable d = pluginCtx.getResources().getDrawable(iconRes, pluginCtx.getTheme());
            if (d == null) return null;
            Bitmap bmp = Bitmap.createBitmap(ICON_PX, ICON_PX, Bitmap.Config.ARGB_8888);
            Canvas c = new Canvas(bmp);
            d.setBounds(0, 0, ICON_PX, ICON_PX);
            d.draw(c);

            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            bmp.compress(Bitmap.CompressFormat.PNG, 100, bos);
            bmp.recycle();
            String b64 = Base64.encodeToString(bos.toByteArray(), Base64.NO_WRAP);
            MapDataRef ref = MapDataRef.parseUri(com.atakmap.android.maps.Base64MapDataRef.toUri(b64));
            if (ref == null) return null;

            // Same ref for every button state so no state ever dereferences a null icon.
            return new WidgetIcon.Builder()
                    .setImageRef(0, ref)
                    .setImageRef(AbstractButtonWidget.STATE_PRESSED, ref)
                    .setImageRef(AbstractButtonWidget.STATE_SELECTED, ref)
                    .setImageRef(AbstractButtonWidget.STATE_DISABLED, ref)
                    .setAnchor(ICON_PX / 2, ICON_PX / 2)
                    .setSize(ICON_PX, ICON_PX)
                    .build();
        } catch (Throwable t) {
            Log.w(TAG, "icon build failed for res " + iconRes + ": " + t.getMessage());
            return null;
        }
    }
}
