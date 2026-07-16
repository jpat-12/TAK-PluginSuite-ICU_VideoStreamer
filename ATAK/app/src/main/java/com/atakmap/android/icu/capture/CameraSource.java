package com.atakmap.android.icu.capture;

import android.content.Context;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Range;
import android.view.Surface;


import com.atakmap.coremap.log.Log;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;

/**
 * PHASE 1 — Camera2 capture.
 *
 * <p>Opens the selected camera and drives up to two output Surfaces simultaneously
 * (Option A in ARCHITECTURE.md §4): the {@link H264Encoder} input Surface (→ RTSP in
 * later phases) and an optional preview Surface for the local operator pane.</p>
 *
 * <p>This is the piece the drone normally provided; here the phone becomes the source.
 * Caller must have already been granted {@code android.permission.CAMERA}.</p>
 */
public class CameraSource {

    private static final String TAG = "ICU.CameraSource";

    public interface Callback {
        void onError(String message);
    }

    private CameraDevice         cameraDevice;
    private CameraCaptureSession captureSession;
    private HandlerThread        cameraThread;
    private Handler              cameraHandler;
    private final Semaphore      cameraLock = new Semaphore(1);

    private Surface encoderSurface;
    private Surface previewSurface;
    private int     fps = 30;
    private volatile int sensorOrientation = 0;
    private volatile boolean frontFacing = false;

    /** Camera sensor mount orientation (0/90/180/270) — for the preview transform. */
    public int getSensorOrientation() { return sensorOrientation; }
    public boolean isFrontFacing() { return frontFacing; }

    /**
     * Open the camera and begin the repeating capture into the given surfaces.
     *
     * @param encoderSurface required — from {@link H264Encoder#start}
     * @param previewSurface optional — may be null (encoder-only)
     */
    public void start(Context ctx, EncoderConfig config,
                      Surface encoderSurface, Surface previewSurface, Callback cb) {
        this.encoderSurface = encoderSurface;
        this.previewSurface = previewSurface;
        this.fps            = config.fps;

        cameraThread  = new HandlerThread("ICU-CameraThread");
        cameraThread.start();
        cameraHandler = new Handler(cameraThread.getLooper());

        CameraManager manager = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
        try {
            String cameraId = selectCamera(manager, config.useFrontCamera);
            if (cameraId == null) { cb.onError("No camera found"); return; }
            if (!cameraLock.tryAcquire(2500, TimeUnit.MILLISECONDS)) {
                cb.onError("Timed out acquiring camera");
                return;
            }
            // SecurityException here means the CAMERA permission wasn't actually granted.
            manager.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice camera) {
                    cameraLock.release();
                    cameraDevice = camera;
                    startCaptureSession(camera, cb);
                }
                @Override public void onDisconnected(CameraDevice camera) {
                    cameraLock.release();
                    camera.close();
                    cameraDevice = null;
                }
                @Override public void onError(CameraDevice camera, int error) {
                    cameraLock.release();
                    camera.close();
                    cameraDevice = null;
                    cb.onError("Camera error " + error);
                }
            }, cameraHandler);
        } catch (SecurityException e) {
            cameraLock.release();
            cb.onError("Camera permission not granted");
        } catch (Exception e) {
            cameraLock.release();
            cb.onError("openCamera: " + e.getMessage());
        }
    }

    public void stop() {
        try { if (captureSession != null) { captureSession.close(); captureSession = null; } }
        catch (Exception ignored) {}
        try { if (cameraDevice != null) { cameraDevice.close(); cameraDevice = null; } }
        catch (Exception ignored) {}
        if (cameraThread != null) {
            cameraThread.quitSafely();
            cameraThread = null;
            cameraHandler = null;
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void startCaptureSession(CameraDevice camera, Callback cb) {
        try {
            List<Surface> targets = new ArrayList<>();
            targets.add(encoderSurface);
            if (previewSurface != null) targets.add(previewSurface);

            camera.createCaptureSession(targets, new CameraCaptureSession.StateCallback() {
                @Override public void onConfigured(CameraCaptureSession session) {
                    captureSession = session;
                    try {
                        CaptureRequest.Builder req =
                                camera.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
                        req.addTarget(encoderSurface);
                        if (previewSurface != null) req.addTarget(previewSurface);
                        req.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE,
                                new Range<>(fps, fps));
                        session.setRepeatingRequest(req.build(), null, cameraHandler);
                    } catch (CameraAccessException e) {
                        cb.onError("Repeating request: " + e.getMessage());
                    }
                }
                @Override public void onConfigureFailed(CameraCaptureSession session) {
                    cb.onError("Capture session configuration failed");
                }
            }, cameraHandler);
        } catch (CameraAccessException e) {
            cb.onError("createCaptureSession: " + e.getMessage());
        }
    }

    private String selectCamera(CameraManager manager, boolean useFront)
            throws CameraAccessException {
        int target = useFront ? CameraCharacteristics.LENS_FACING_FRONT
                              : CameraCharacteristics.LENS_FACING_BACK;
        String[] ids = manager.getCameraIdList();
        for (String id : ids) {
            android.hardware.camera2.CameraCharacteristics ch =
                    manager.getCameraCharacteristics(id);
            Integer facing = ch.get(CameraCharacteristics.LENS_FACING);
            if (facing != null && facing == target) {
                Integer so = ch.get(CameraCharacteristics.SENSOR_ORIENTATION);
                if (so != null) sensorOrientation = so;
                frontFacing = (facing == CameraCharacteristics.LENS_FACING_FRONT);
                return id;
            }
        }
        return ids.length > 0 ? ids[0] : null;
    }
}
