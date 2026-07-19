package com.atakmap.android.icu.ui.qr;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.graphics.SurfaceTexture;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.Image;
import android.media.ImageReader;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;

import com.atakmap.coremap.log.Log;
import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.NotFoundException;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;

import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Minimal self-contained QR scanner: Camera2 preview + a ZXing decode running on
 * every frame. No third-party scanning UI (no zxing-android-embedded) — just a
 * {@link TextureView} for the operator to aim with, and an {@link ImageReader}
 * feeding decode attempts, mirroring the same dual-Surface Camera2 pattern
 * {@code capture/CameraSource} already uses for the broadcast path.
 *
 * <p>Plugin-hosted Activities run outside ATAK's own classloader (same constraint
 * noted in the SDK's helloworld sample's CameraActivity), so the result can't be
 * handed back via a normal method call — it's broadcast as {@link #ACTION_RESULT}
 * and the caller (ICUVideoDropDownReceiver) listens for it while its settings
 * dialog is open.</p>
 */
public class QrScanActivity extends Activity {

    private static final String TAG = "ICU.QrScanActivity";
    private static final int REQ_CAMERA = 9421;

    public static final String ACTION_RESULT = "com.atakmap.android.icu.QR_SCAN_RESULT";
    public static final String EXTRA_TEXT = "text";
    public static final String EXTRA_CANCELLED = "cancelled";

    private final MultiFormatReader reader = new MultiFormatReader();
    private final AtomicBoolean resultDelivered = new AtomicBoolean(false);
    private final Semaphore cameraLock = new Semaphore(1);

    private TextureView previewView;
    private CameraDevice cameraDevice;
    private CameraCaptureSession captureSession;
    private ImageReader imageReader;
    private HandlerThread cameraThread;
    private Handler cameraHandler;
    private Surface previewSurface;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, EnumSet.of(BarcodeFormat.QR_CODE));
        reader.setHints(hints);

        setContentView(buildUi());

        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            startCameraThread();
        } else {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_CAMERA);
        }
    }

    // ── UI (fully programmatic — no plugin layout resources to resolve here) ──────

    private View buildUi() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        previewView = new TextureView(this);
        root.addView(previewView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        previewView.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int w, int h) {
                previewSurface = new Surface(st);
                maybeStartCaptureSession();
            }
            @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) {}
            @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) {
                previewSurface = null;
                return true;
            }
            @Override public void onSurfaceTextureUpdated(SurfaceTexture st) {}
        });

        float d = getResources().getDisplayMetrics().density;

        TextView hint = new TextView(this);
        hint.setText("Point the camera at the Stream URL QR code");
        hint.setTextColor(Color.WHITE);
        hint.setBackgroundColor(0x99000000);
        hint.setPadding((int) (16 * d), (int) (10 * d), (int) (16 * d), (int) (10 * d));
        FrameLayout.LayoutParams hintLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        hintLp.gravity = Gravity.TOP | Gravity.CENTER_HORIZONTAL;
        hintLp.topMargin = (int) (24 * d);
        root.addView(hint, hintLp);

        Button cancel = new Button(this);
        cancel.setText("Cancel");
        cancel.setOnClickListener(v -> finishCancelled());
        FrameLayout.LayoutParams cancelLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cancelLp.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        cancelLp.bottomMargin = (int) (32 * d);
        root.addView(cancel, cancelLp);

        return root;
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        if (requestCode == REQ_CAMERA) {
            if (results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) {
                startCameraThread();
            } else {
                finishCancelled();
            }
        }
    }

    // ── Camera2 (decode-only ImageReader + a visible TextureView preview) ─────────

    private void startCameraThread() {
        cameraThread = new HandlerThread("ICU-QRCamera");
        cameraThread.start();
        cameraHandler = new Handler(cameraThread.getLooper());

        imageReader = ImageReader.newInstance(1280, 720, ImageFormat.YUV_420_888, 2);
        imageReader.setOnImageAvailableListener(this::onFrame, cameraHandler);

        CameraManager manager = (CameraManager) getSystemService(CAMERA_SERVICE);
        try {
            String cameraId = selectBackCamera(manager);
            if (cameraId == null) {
                Log.w(TAG, "No camera available");
                finishCancelled();
                return;
            }
            if (!cameraLock.tryAcquire(2500, TimeUnit.MILLISECONDS)) {
                finishCancelled();
                return;
            }
            manager.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice camera) {
                    cameraLock.release();
                    cameraDevice = camera;
                    maybeStartCaptureSession();
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
                    Log.w(TAG, "Camera error " + error);
                    finishCancelled();
                }
            }, cameraHandler);
        } catch (SecurityException | CameraAccessException | InterruptedException e) {
            cameraLock.release();
            Log.w(TAG, "startCameraThread: " + e.getMessage());
            finishCancelled();
        }
    }

    private void maybeStartCaptureSession() {
        if (cameraDevice == null || imageReader == null) return;
        try {
            List<Surface> targets = new ArrayList<>();
            targets.add(imageReader.getSurface());
            if (previewSurface != null) targets.add(previewSurface);

            cameraDevice.createCaptureSession(targets, new CameraCaptureSession.StateCallback() {
                @Override public void onConfigured(CameraCaptureSession session) {
                    captureSession = session;
                    try {
                        CaptureRequest.Builder req = cameraDevice.createCaptureRequest(
                                CameraDevice.TEMPLATE_PREVIEW);
                        req.addTarget(imageReader.getSurface());
                        if (previewSurface != null) req.addTarget(previewSurface);
                        session.setRepeatingRequest(req.build(), null, cameraHandler);
                    } catch (CameraAccessException e) {
                        Log.w(TAG, "setRepeatingRequest: " + e.getMessage());
                    }
                }
                @Override public void onConfigureFailed(CameraCaptureSession session) {
                    Log.w(TAG, "QR capture session configuration failed");
                    finishCancelled();
                }
            }, cameraHandler);
        } catch (CameraAccessException e) {
            Log.w(TAG, "createCaptureSession: " + e.getMessage());
            finishCancelled();
        }
    }

    private String selectBackCamera(CameraManager manager) throws CameraAccessException {
        String[] ids = manager.getCameraIdList();
        for (String id : ids) {
            CameraCharacteristics ch = manager.getCameraCharacteristics(id);
            Integer facing = ch.get(CameraCharacteristics.LENS_FACING);
            if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) return id;
        }
        return ids.length > 0 ? ids[0] : null;
    }

    // ── Decode ──────────────────────────────────────────────────────────────────

    private void onFrame(ImageReader reader) {
        Image image = reader.acquireLatestImage();
        if (image == null) return;
        try {
            if (resultDelivered.get()) return;

            Image.Plane yPlane = image.getPlanes()[0];
            ByteBuffer buffer = yPlane.getBuffer();
            int rowStride = yPlane.getRowStride();
            byte[] data = new byte[buffer.remaining()];
            buffer.get(data);

            PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                    data, rowStride, image.getHeight(), 0, 0,
                    image.getWidth(), image.getHeight(), false);
            BinaryBitmap bitmap = new BinaryBitmap(new HybridBinarizer(source));
            try {
                Result result = this.reader.decodeWithState(bitmap);
                deliverResult(result.getText());
            } catch (NotFoundException notFound) {
                // no QR in this frame — normal while aiming, just try the next one
            } finally {
                this.reader.reset();
            }
        } catch (Throwable t) {
            Log.w(TAG, "onFrame decode failed: " + t.getMessage());
        } finally {
            image.close();
        }
    }

    private void deliverResult(String text) {
        if (!resultDelivered.compareAndSet(false, true)) return;
        Intent result = new Intent(ACTION_RESULT).setPackage(getPackageName())
                .putExtra(EXTRA_TEXT, text);
        sendBroadcast(result);
        runOnUiThread(this::finish);
    }

    private void finishCancelled() {
        if (!resultDelivered.compareAndSet(false, true)) return;
        Intent result = new Intent(ACTION_RESULT).setPackage(getPackageName())
                .putExtra(EXTRA_CANCELLED, true);
        sendBroadcast(result);
        runOnUiThread(this::finish);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        try { if (captureSession != null) { captureSession.close(); captureSession = null; } }
        catch (Exception ignored) {}
        try { if (cameraDevice != null) { cameraDevice.close(); cameraDevice = null; } }
        catch (Exception ignored) {}
        if (imageReader != null) { imageReader.close(); imageReader = null; }
        if (cameraThread != null) { cameraThread.quitSafely(); cameraThread = null; cameraHandler = null; }
    }
}
