package com.atakmap.android.icu;

import android.content.Context;
import android.content.Intent;

import com.atakmap.android.icu.ui.StreamStatusWidget;
import com.atakmap.android.ipc.AtakBroadcast;
import com.atakmap.android.maps.MapView;
import com.atakmap.android.widgets.AbstractWidgetMapComponent;

/**
 * MapComponent for ICU VideoStreamer — creates and registers the drop-down pane
 * receiver, and owns its lifecycle.
 *
 * <p>Extends {@link AbstractWidgetMapComponent} (rather than the plain
 * AbstractMapComponent) so it also owns a {@code LayoutWidget} that renders
 * directly on the map, independent of the drop-down pane's open/closed state —
 * this is what backs {@link StreamStatusWidget}, the persistent broadcast-status
 * indicator that stays visible after the plugin panel is closed.</p>
 */
public class ICUVideoMapComponent extends AbstractWidgetMapComponent {

    private ICUVideoDropDownReceiver dropDown;
    private StreamStatusWidget statusWidget;

    @Override
    protected void onCreateWidgets(Context context, Intent intent, MapView mapView) {
        // context here is the plugin context (from the plugin classloader).
        statusWidget = new StreamStatusWidget(mapView, context, getRootLayoutWidget());
        dropDown = new ICUVideoDropDownReceiver(mapView, context, statusWidget);

        AtakBroadcast.DocumentedIntentFilter filter =
                new AtakBroadcast.DocumentedIntentFilter();
        filter.addAction(ICUVideoDropDownReceiver.SHOW,
                "Show the ICU VideoStreamer panel");
        registerReceiver(context, dropDown, filter);
    }

    @Override
    protected void onDestroyWidgets(Context context, MapView mapView) {
        if (dropDown != null) {
            unregisterReceiver(context, dropDown);
            dropDown.dispose();
            dropDown = null;
        }
        if (statusWidget != null) {
            statusWidget.dispose();
            statusWidget = null;
        }
    }
}
