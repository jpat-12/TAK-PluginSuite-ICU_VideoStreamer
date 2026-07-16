package com.atakmap.android.icu;

import android.content.Context;
import android.content.Intent;

import com.atakmap.android.ipc.AtakBroadcast;
import com.atakmap.android.maps.AbstractMapComponent;
import com.atakmap.android.maps.MapView;

/**
 * AbstractMapComponent for ICU VideoStreamer — creates and registers the
 * drop-down pane receiver, and owns its lifecycle.
 */
public class ICUVideoMapComponent extends AbstractMapComponent {

    private ICUVideoDropDownReceiver dropDown;

    @Override
    public void onCreate(Context context, Intent intent, MapView mapView) {
        // context here is the plugin context (from the plugin classloader).
        dropDown = new ICUVideoDropDownReceiver(mapView, context);

        AtakBroadcast.DocumentedIntentFilter filter =
                new AtakBroadcast.DocumentedIntentFilter();
        filter.addAction(ICUVideoDropDownReceiver.SHOW,
                "Show the ICU VideoStreamer panel");
        registerReceiver(context, dropDown, filter);
    }

    @Override
    protected void onDestroyImpl(Context context, MapView mapView) {
        if (dropDown != null) {
            unregisterReceiver(context, dropDown);
            dropDown.dispose();
            dropDown = null;
        }
    }
}
