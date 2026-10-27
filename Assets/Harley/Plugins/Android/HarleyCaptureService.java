package com.harley.capture;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.SystemClock;

import java.nio.ByteBuffer;

/**
 * Foreground service that mirrors what the headset shows into an ImageReader and keeps the
 * newest frame (about two per second) for Harley. Android 14 rules: the service starts in the
 * foreground with type mediaProjection BEFORE asking for the projection, and registers a
 * callback before creating the virtual display.
 */
public class HarleyCaptureService extends Service {
    public static final String EXTRA_RESULT_CODE = "harley_result_code";
    public static final String EXTRA_RESULT_DATA = "harley_result_data";

    private static final String CHANNEL = "harley_screen";
    private static final int NOTIFICATION_ID = 4317;
    private static final int WIDTH = 1280, HEIGHT = 720;
    private static final long FRAME_INTERVAL_MS = 450;

    private MediaProjection projection;
    private VirtualDisplay display;
    private ImageReader reader;
    private HandlerThread thread;
    private Handler handler;
    private long lastFrame;
    private boolean cleaned;

    @Override public IBinder onBind(Intent intent) { return null; }

    @SuppressWarnings("deprecation")
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        goForeground();
        if (intent == null) { stopSelf(); return START_NOT_STICKY; }

        int code = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
        Intent data = intent.getParcelableExtra(EXTRA_RESULT_DATA);
        MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        try {
            projection = (mpm != null && data != null) ? mpm.getMediaProjection(code, data) : null;
        } catch (Throwable t) {
            HarleyCapture.lastError = t.toString();
        }
        if (projection == null) {
            if (HarleyCapture.lastError.isEmpty()) HarleyCapture.lastError = "no projection";
            stopSelf();
            return START_NOT_STICKY;
        }

        thread = new HandlerThread("HarleyCapture");
        thread.start();
        handler = new Handler(thread.getLooper());

        projection.registerCallback(new MediaProjection.Callback() {
            @Override public void onStop() {
                HarleyCapture.running = false;
                stopSelf();
            }
        }, handler);

        int dpi = getResources().getDisplayMetrics().densityDpi;
        reader = ImageReader.newInstance(WIDTH, HEIGHT, PixelFormat.RGBA_8888, 2);
        reader.setOnImageAvailableListener(new ImageReader.OnImageAvailableListener() {
            @Override public void onImageAvailable(ImageReader r) { onFrame(r); }
        }, handler);
        display = projection.createVirtualDisplay("HarleyScreen", WIDTH, HEIGHT, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, reader.getSurface(), null, handler);

        HarleyCapture.running = true;
        return START_NOT_STICKY;
    }

    private void onFrame(ImageReader r) {
        Image image = null;
        try {
            image = r.acquireLatestImage();
            if (image == null) return;
            long now = SystemClock.uptimeMillis();
            if (now - lastFrame < FRAME_INTERVAL_MS) return; // ~2 frames per second is plenty
            lastFrame = now;

            Image.Plane plane = image.getPlanes()[0];
            ByteBuffer buffer = plane.getBuffer();
            int w = image.getWidth(), h = image.getHeight();
            int pixelStride = plane.getPixelStride();
            int rowPadding = plane.getRowStride() - pixelStride * w;

            Bitmap padded = Bitmap.createBitmap(w + rowPadding / pixelStride, h, Bitmap.Config.ARGB_8888);
            padded.copyPixelsFromBuffer(buffer);
            Bitmap frame = padded;
            if (rowPadding != 0) {
                frame = Bitmap.createBitmap(padded, 0, 0, w, h);
                padded.recycle();
            }

            HarleyCapture.latest = frame;
            HarleyCapture.latestAtMillis = System.currentTimeMillis();
        } catch (Throwable t) {
            HarleyCapture.lastError = t.toString();
        } finally {
            if (image != null) image.close();
        }
    }

    @SuppressWarnings("deprecation")
    private void goForeground() {
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26 && nm != null) {
            nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Harley screen sharing", NotificationManager.IMPORTANCE_LOW));
        }
        Notification.Builder b = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
        Notification n = b.setContentTitle("Harley can see your screen")
                .setContentText("Close Harley to stop.")
                .setSmallIcon(android.R.drawable.ic_menu_view)
                .setOngoing(true)
                .build();
        if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        else startForeground(NOTIFICATION_ID, n);
    }

    @Override public void onDestroy() {
        cleanup();
        super.onDestroy();
    }

    private void cleanup() {
        if (cleaned) return;
        cleaned = true;
        HarleyCapture.running = false;
        if (display != null) { display.release(); display = null; }
        if (reader != null) { reader.close(); reader = null; }
        if (projection != null) { try { projection.stop(); } catch (Throwable ignored) { } projection = null; }
        if (thread != null) { thread.quitSafely(); thread = null; }
    }
}
