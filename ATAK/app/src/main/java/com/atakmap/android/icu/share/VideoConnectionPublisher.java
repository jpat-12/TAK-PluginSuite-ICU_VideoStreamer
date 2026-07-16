package com.atakmap.android.icu.share;

/**
 * PHASE 3 — Register the live stream with ATAK's local video framework so it appears
 * in the built-in Video tool (via {@code gov.tak.api.video.ConnectionEntry} +
 * {@code IVideoConnectionManager}).
 *
 * <p>Companion to {@link VideoCotBroadcaster}, which handles network dissemination
 * of the URL over CoT.</p>
 */
public class VideoConnectionPublisher {

    public void publish(String rtspUrl, String alias) {
        throw new UnsupportedOperationException("VideoConnectionPublisher.publish — Phase 3");
    }

    public void unpublish() {
        // TODO(Phase 3): remove the ConnectionEntry from the manager.
    }
}
