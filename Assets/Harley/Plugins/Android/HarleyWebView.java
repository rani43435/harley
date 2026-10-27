package com.harley.web;

import android.app.Activity;
import android.app.Presentation;
import android.content.Context;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * A real Android WebView (browser, YouTube...) drawn off-screen for Harley.
 *
 * The page renders into a private virtual display. Every new frame is copied
 * into a direct ByteBuffer that Unity uploads into a texture, so the panel can
 * float anywhere in the room and Harley can look at exactly what is on it.
 * Touches from hands / controllers are sent back as touch events.
 */
public class HarleyWebView {
    private static final String TAG = "HarleyWeb";

    private final Activity activity;
    private final int width, height;
    private final ByteBuffer frame;
    private volatile int frameCount = 0;
    private volatile boolean ready = false;
    private volatile String title = "", url = "";
    private volatile boolean loading = false;

    private HandlerThread thread;
    private ImageReader reader;
    private VirtualDisplay display;
    private Presentation presentation;
    private FrameLayout root;
    private WebView web;
    private View fullscreenView;
    private WebChromeClient.CustomViewCallback fullscreenCallback;
    private long downTime;

    public HarleyWebView(final Activity activity, int width, int height, final int dpi, final String startUrl) {
        this.activity = activity;
        this.width = width;
        this.height = height;
        this.frame = ByteBuffer.allocateDirect(width * height * 4).order(ByteOrder.nativeOrder());

        activity.runOnUiThread(new Runnable() {
            @Override public void run() {
                try { create(dpi, startUrl); }
                catch (Throwable t) { Log.e(TAG, "Could not create the web view", t); }
            }
        });
    }

    private void create(int dpi, String startUrl) {
        thread = new HandlerThread("HarleyWebFrames");
        thread.start();
        Handler handler = new Handler(thread.getLooper());

        reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 3);
        reader.setOnImageAvailableListener(new ImageReader.OnImageAvailableListener() {
            @Override public void onImageAvailable(ImageReader r) { copyFrame(r); }
        }, handler);

        DisplayManager dm = (DisplayManager) activity.getSystemService(Context.DISPLAY_SERVICE);
        display = dm.createVirtualDisplay("HarleyWeb", width, height, dpi, reader.getSurface(),
                DisplayManager.VIRTUAL_DISPLAY_FLAG_PRESENTATION | DisplayManager.VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY);

        presentation = new Presentation(activity, display.getDisplay());
        root = new FrameLayout(presentation.getContext());
        root.setBackgroundColor(Color.WHITE);

        web = new WebView(presentation.getContext());
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setUseWideViewPort(true);
        s.setLoadWithOverviewMode(true);
        s.setBuiltInZoomControls(false);
        s.setSupportMultipleWindows(false);
        s.setJavaScriptCanOpenWindowsAutomatically(false);

        web.setWebViewClient(new WebViewClient() {
            @Override public void onPageStarted(WebView v, String u, android.graphics.Bitmap icon) { loading = true; url = u; }
            @Override public void onPageFinished(WebView v, String u) { loading = false; url = u; String t = v.getTitle(); title = t != null ? t : ""; }
        });
        web.setWebChromeClient(new WebChromeClient() {
            @Override public void onReceivedTitle(WebView v, String t) { title = t != null ? t : ""; }
            // Video fullscreen (YouTube): show the video view over the page inside the same panel.
            @Override public void onShowCustomView(View view, CustomViewCallback callback) {
                if (fullscreenView != null) { callback.onCustomViewHidden(); return; }
                fullscreenView = view;
                fullscreenCallback = callback;
                root.addView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            }
            @Override public void onHideCustomView() { exitFullscreen(); }
        });

        root.addView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        presentation.setContentView(root);
        presentation.show();
        if (startUrl != null && startUrl.length() > 0) web.loadUrl(startUrl);
        ready = true;
    }

    private void exitFullscreen() {
        if (fullscreenView == null) return;
        root.removeView(fullscreenView);
        fullscreenView = null;
        if (fullscreenCallback != null) fullscreenCallback.onCustomViewHidden();
        fullscreenCallback = null;
    }

    private void copyFrame(ImageReader r) {
        Image img = null;
        try {
            img = r.acquireLatestImage();
            if (img == null) return;
            Image.Plane plane = img.getPlanes()[0];
            ByteBuffer src = plane.getBuffer();
            int rowStride = plane.getRowStride();
            int rowBytes = width * 4;
            synchronized (frame) {
                frame.clear();
                if (rowStride == rowBytes) {
                    src.limit(Math.min(src.capacity(), rowBytes * height));
                    frame.put(src);
                } else {
                    for (int y = 0; y < height; y++) {
                        int start = y * rowStride;
                        src.limit(start + rowBytes);
                        src.position(start);
                        frame.put(src);
                    }
                }
            }
            frameCount++;
        } catch (Throwable t) {
            Log.w(TAG, "frame copy failed: " + t);
        } finally {
            if (img != null) img.close();
        }
    }

    // ---------- called from Unity ----------

    public boolean isReady() { return ready; }
    public ByteBuffer getBuffer() { return frame; }
    public int getFrameCount() { return frameCount; }
    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public String getTitle() { return title; }
    public String getUrl() { return url; }
    public boolean isLoading() { return loading; }

    public void loadUrl(final String u) {
        ui(new Runnable() { @Override public void run() { if (web != null) { exitFullscreen(); web.loadUrl(u); } } });
    }

    public void goBack() {
        ui(new Runnable() { @Override public void run() {
            if (fullscreenView != null) { exitFullscreen(); return; }
            if (web != null && web.canGoBack()) web.goBack();
        } });
    }

    public void goForward() { ui(new Runnable() { @Override public void run() { if (web != null && web.canGoForward()) web.goForward(); } }); }
    public void reload() { ui(new Runnable() { @Override public void run() { if (web != null) web.reload(); } }); }

    /** action: 0 down, 1 up, 2 move. x, y in pixels from the top-left. */
    public void touch(final int action, final float x, final float y) {
        ui(new Runnable() { @Override public void run() {
            View target = fullscreenView != null ? fullscreenView : web;
            if (target == null) return;
            long now = SystemClock.uptimeMillis();
            int a = action == 0 ? MotionEvent.ACTION_DOWN : action == 1 ? MotionEvent.ACTION_UP : MotionEvent.ACTION_MOVE;
            if (a == MotionEvent.ACTION_DOWN) downTime = now;
            MotionEvent ev = MotionEvent.obtain(downTime, now, a, x, y, 0);
            ev.setSource(InputDevice.SOURCE_TOUCHSCREEN);
            root.dispatchTouchEvent(ev);
            ev.recycle();
        } });
    }

    public void scroll(final float dx, final float dy) {
        ui(new Runnable() { @Override public void run() {
            if (web != null) web.evaluateJavascript("window.scrollBy(" + dx + "," + dy + ");", null);
        } });
    }

    /** Types text into whatever field is focused on the page. */
    public void typeText(final String text) {
        final String js =
            "(function(t){var e=document.activeElement;if(!e)return;" +
            "if(e.isContentEditable){document.execCommand('insertText',false,t);return;}" +
            "if(!('value' in e))return;" +
            "var s=e.selectionStart!=null?e.selectionStart:e.value.length,f=e.selectionEnd!=null?e.selectionEnd:s;" +
            "var v=e.value.slice(0,s)+t+e.value.slice(f);" +
            "var p=Object.getPrototypeOf(e),d=Object.getOwnPropertyDescriptor(p,'value');" +
            "if(d&&d.set)d.set.call(e,v);else e.value=v;" +
            "e.dispatchEvent(new Event('input',{bubbles:true}));})(" + quote(text) + ");";
        ui(new Runnable() { @Override public void run() { if (web != null) web.evaluateJavascript(js, null); } });
    }

    public void pressEnter() {
        ui(new Runnable() { @Override public void run() {
            if (web == null) return;
            web.dispatchKeyEvent(new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER));
            web.dispatchKeyEvent(new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER));
        } });
    }

    public void setPaused(final boolean paused) {
        ui(new Runnable() { @Override public void run() {
            if (web == null) return;
            if (paused) web.onPause(); else web.onResume();
        } });
    }

    public void destroy() {
        ui(new Runnable() { @Override public void run() {
            try {
                if (web != null) { web.stopLoading(); web.destroy(); web = null; }
                if (presentation != null) { presentation.dismiss(); presentation = null; }
                if (display != null) { display.release(); display = null; }
                if (reader != null) { reader.close(); reader = null; }
                if (thread != null) { thread.quitSafely(); thread = null; }
            } catch (Throwable t) { Log.w(TAG, "destroy: " + t); }
        } });
    }

    private void ui(Runnable r) { activity.runOnUiThread(r); }

    private static String quote(String s) {
        StringBuilder b = new StringBuilder("\"");
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '<': b.append("\\u003c"); break;
                default:
                    if (c < 0x20 || c == 0x2028 || c == 0x2029) b.append(String.format("\\u%04x", (int) c));
                    else b.append(c);
            }
        }
        return b.append('"').toString();
    }
}
