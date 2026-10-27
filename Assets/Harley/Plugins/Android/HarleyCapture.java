package com.harley.capture;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Bitmap;
import android.util.Base64;

import java.io.ByteArrayOutputStream;

/**
 * Harley's screen sharing (Android MediaProjection), called from Unity (ScreenEyes.cs).
 *
 * requestStart(activity)  shows Quest's "allow screen capture" prompt, then starts HarleyCaptureService
 * isRunning()             true while frames are coming in
 * grabJpegBase64(max, q)  latest frame, scaled so its longest edge is max px, as base64 JPEG (or null)
 * stop(activity)          stops capturing
 */
public final class HarleyCapture {
    static volatile Bitmap latest;
    static volatile long latestAtMillis;
    static volatile boolean running;
    static volatile String lastError = "";

    private HarleyCapture() { }

    public static void requestStart(final Activity activity) {
        if (running || activity == null) return;
        lastError = "";
        activity.runOnUiThread(new Runnable() {
            @Override public void run() {
                activity.startActivity(new Intent(activity, HarleyCapturePermissionActivity.class));
            }
        });
    }

    public static boolean isRunning() { return running; }

    public static String getLastError() { return lastError; }

    public static long getLatestAgeMillis() {
        return latest == null ? -1 : System.currentTimeMillis() - latestAtMillis;
    }

    public static void stop(Activity activity) {
        if (activity == null) return;
        activity.stopService(new Intent(activity, HarleyCaptureService.class));
    }

    public static String grabJpegBase64(int maxSize, int quality) {
        Bitmap src = latest;
        if (src == null || src.isRecycled()) return null;
        try {
            int w = src.getWidth(), h = src.getHeight();
            float scale = Math.min(1f, maxSize / (float) Math.max(w, h));
            Bitmap out = scale < 1f
                ? Bitmap.createScaledBitmap(src, Math.max(16, Math.round(w * scale)), Math.max(16, Math.round(h * scale)), true)
                : src;
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            out.compress(Bitmap.CompressFormat.JPEG, Math.max(30, Math.min(95, quality)), bytes);
            return Base64.encodeToString(bytes.toByteArray(), Base64.NO_WRAP);
        } catch (Throwable t) {
            lastError = t.toString();
            return null;
        }
    }
}
