package com.atakmap.android.icu.capture;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.view.Surface;

import com.atakmap.coremap.log.Log;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.Arrays;

/**
 * PHASE 1 — MediaCodec H.264 surface-input encoder.
 *
 * <p>Provides an input {@link Surface} for {@link CameraSource}, then drains the
 * encoded bitstream on a background thread and emits SPS/PPS plus individual NAL
 * units to a {@link Callback}. In Phase 1 the NALs are just counted/logged; Phase 2's
 * {@code serve/RtspServer} consumes them for RTP fan-out.</p>
 */
public class H264Encoder {

    private static final String TAG  = "ICU.H264Encoder";
    private static final String MIME = "video/avc";

    public interface Callback {
        void onSpsReady(byte[] sps);
        void onPpsReady(byte[] pps);
        void onNalUnit(byte[] data, boolean isKeyFrame, long ptsUs);
        void onError(String message);
    }

    private MediaCodec       codec;
    private Surface          inputSurface;
    private Thread           drainThread;
    private volatile boolean running;

    /**
     * Configure and start the encoder.
     *
     * @return the encoder input {@link Surface} to hand to {@link CameraSource},
     *         or {@code null} if setup failed (the callback receives the error).
     */
    public Surface start(EncoderConfig config, Callback callback) {
        try {
            MediaFormat fmt = MediaFormat.createVideoFormat(
                    MIME, config.resolution.w, config.resolution.h);
            fmt.setInteger(MediaFormat.KEY_BIT_RATE, config.bitrateKbps * 1000);
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, config.fps);
            fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, config.gopSeconds);
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            // Best-effort rotation hint (honored by some encoders; ignored by others).
            if (config.rotationDegrees >= 0) {
                try { fmt.setInteger(MediaFormat.KEY_ROTATION, config.rotationDegrees); }
                catch (Exception ignored) {}
            }

            codec = MediaCodec.createEncoderByType(MIME);
            codec.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            inputSurface = codec.createInputSurface();
            codec.start();
            running = true;

            startDrainThread(callback);
            return inputSurface;
        } catch (IOException | IllegalStateException e) {
            callback.onError("Encoder start: " + e.getMessage());
            stop();
            return null;
        }
    }

    public void stop() {
        running = false;
        if (drainThread != null) {
            try { drainThread.join(500); } catch (InterruptedException ignored) {}
            drainThread = null;
        }
        if (codec != null) {
            try {
                codec.stop();
            } catch (Exception e) {
                Log.w(TAG, "codec.stop: " + e.getMessage());
            }
            try { codec.release(); } catch (Exception ignored) {}
            codec = null;
        }
        if (inputSurface != null) {
            try { inputSurface.release(); } catch (Exception ignored) {}
            inputSurface = null;
        }
    }

    // ── Output drain ───────────────────────────────────────────────────────────

    private void startDrainThread(Callback cb) {
        drainThread = new Thread(() -> {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            while (running) {
                int idx;
                try {
                    idx = codec.dequeueOutputBuffer(info, 10_000);
                } catch (IllegalStateException e) {
                    break; // codec torn down
                }
                if (idx < 0) continue;
                try {
                    ByteBuffer buf = codec.getOutputBuffer(idx);
                    if (buf != null && info.size > 0) {
                        byte[] data = new byte[info.size];
                        buf.position(info.offset);
                        buf.get(data);

                        boolean isConfig = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                        if (isConfig) {
                            parseSpsAndPps(data, cb);
                        } else {
                            boolean isKey = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
                            cb.onNalUnit(data, isKey, info.presentationTimeUs);
                        }
                    }
                } finally {
                    try { codec.releaseOutputBuffer(idx, false); } catch (Exception ignored) {}
                }
            }
        }, "ICU-CodecDrain");
        drainThread.start();
    }

    /** CODEC_CONFIG output holds SPS + PPS concatenated, each with a start code. */
    private void parseSpsAndPps(byte[] data, Callback cb) {
        int i = 0;
        while (i < data.length - 4) {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) {
                int end = data.length;
                for (int j = i + 4; j < data.length - 3; j++) {
                    if (data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 0 && data[j + 3] == 1) {
                        end = j;
                        break;
                    }
                }
                byte[] nal = Arrays.copyOfRange(data, i, end);
                int nalType = nal[4] & 0x1F;
                if (nalType == 7)      cb.onSpsReady(nal);
                else if (nalType == 8) cb.onPpsReady(nal);
                i = end;
            } else {
                i++;
            }
        }
    }
}
