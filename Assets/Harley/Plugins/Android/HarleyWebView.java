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
    private String normalUa, desktopUa;
    private volatile int identity = 0; // 0 phone (normal), 1 desktop, 2 TV for youtube.com/tv

    // A text field on the page got focus: Harley opens her keyboard for it
    private volatile int focusSeq = 0;
    private volatile String focusValue = "";
    private volatile boolean inputFocused = false;

    private class InputBridge {
        @android.webkit.JavascriptInterface public void focus(String value, String type) {
            if ("password".equals(type)) value = "";
            focusValue = value != null ? value : ""; inputFocused = true; focusSeq++;
        }
        @android.webkit.JavascriptInterface public void blur() { inputFocused = false; }
    }

    private static final String FOCUS_JS =
        "(function(){if(window.__harleyFocus)return;window.__harleyFocus=1;" +
        "function ok(e){if(!e)return false;if(e.isContentEditable)return true;var t=(e.tagName||'').toLowerCase();" +
        "if(t=='textarea')return true;if(t!='input')return false;var y=(e.type||'text').toLowerCase();" +
        "return ['text','search','email','url','tel','password','number'].indexOf(y)>=0;}" +
        "function real(v){var e=(v.composedPath&&v.composedPath()[0])||v.target;return e;}" +
        "document.addEventListener('focusin',function(v){var e=real(v);if(ok(e))HarleyInput.focus(e.value||e.innerText||'',e.type||'');},true);" +
        "document.addEventListener('focusout',function(v){if(ok(real(v)))HarleyInput.blur();},true);" +
        "})();";

    /** youtube.com/tv only opens for TVs: same Samsung TV browser identity as the Ambient Video app. */
    private static final String TV_UA = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 76.0.3809.146/6.0 TV Safari/537.36";

    private static boolean isTv(String u) { return u != null && u.contains("youtube.com/tv"); }

    /** Switch the browser identity before loading a page (TV for YouTube TV, normal otherwise). */
    private void uaFor(String u) {
        if (web == null) return;
        String want = isTv(u) ? TV_UA : identity == 1 && desktopUa != null ? desktopUa : normalUa;
        if (!want.equals(web.getSettings().getUserAgentString())) web.getSettings().setUserAgentString(want);
    }

    public HarleyWebView(final Activity activity, int width, int height, final int dpi, final String startUrl, int identity) {
        this.activity = activity;
        this.identity = identity;
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
        normalUa = s.getUserAgentString();
        // Same Chrome version, desktop identity (sites like WhatsApp Web need it)
        java.util.regex.Matcher m = java.util.regex.Pattern.compile("Chrome/[\\d.]+").matcher(normalUa);
        desktopUa = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) " + (m.find() ? m.group() : "Chrome/130.0.0.0") + " Safari/537.36";
        web.setInitialScale(0);

        web.setWebViewClient(new WebViewClient() {
            @Override public void onPageStarted(WebView v, String u, android.graphics.Bitmap icon) { loading = true; url = u; }
            @Override public boolean shouldOverrideUrlLoading(WebView v, android.webkit.WebResourceRequest r) {
                String u = r.getUrl().toString();
                if (isTv(u) != isTv(v.getUrl())) { uaFor(u); v.loadUrl(u); return true; }
                return false;
            }
            @Override public void onPageFinished(WebView v, String u) {
                loading = false; url = u; String t = v.getTitle(); title = t != null ? t : "";
                inputFocused = false;
                v.evaluateJavascript(FOCUS_JS, null);
            }
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
        web.addJavascriptInterface(new InputBridge(), "HarleyInput");
        if (startUrl != null && startUrl.length() > 0) { uaFor(startUrl); web.loadUrl(startUrl); }
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
    public int getFocusSeq() { return focusSeq; }
    public String getFocusValue() { return focusValue; }
    public boolean isInputFocused() { return inputFocused; }

    /** Replaces the text of the focused field (Harley's keyboard gives back the whole text). */
    public void setText(final String text) {
        final String js =
            "(function(t){var e=document.activeElement;while(e&&e.shadowRoot&&e.shadowRoot.activeElement)e=e.shadowRoot.activeElement;" +
            "if(e&&e.tagName&&e.tagName.toLowerCase()=='iframe'){try{e=e.contentDocument.activeElement;}catch(x){}}if(!e)return;" +
            "if(e.isContentEditable){e.focus();document.execCommand('selectAll',false,null);document.execCommand('insertText',false,t);return;}" +
            "if(!('value' in e))return;" +
            "var p=Object.getPrototypeOf(e),d=Object.getOwnPropertyDescriptor(p,'value');" +
            "if(d&&d.set)d.set.call(e,t);else e.value=t;" +
            "e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));})(" + quote(text) + ");";
        ui(new Runnable() { @Override public void run() { if (web != null) web.evaluateJavascript(js, null); } });
    }

    public void loadUrl(final String u) {
        ui(new Runnable() { @Override public void run() { if (web != null) { exitFullscreen(); uaFor(u); web.loadUrl(u); } } });
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

    public boolean isTvMode() { return isTv(url); }

    /** 0 phone, 1 desktop. Applies to the next page load. */
    public void setIdentity(int id) { identity = id; }

    /** Presses a key on the page (YouTube TV is driven by arrows, Enter and Back). */
    public void key(final int keyCode) {
        ui(new Runnable() { @Override public void run() {
            View target = fullscreenView != null ? fullscreenView : web;
            if (target == null) return;
            target.requestFocus();
            long now = SystemClock.uptimeMillis();
            target.dispatchKeyEvent(new KeyEvent(now, now, KeyEvent.ACTION_DOWN, keyCode, 0));
            target.dispatchKeyEvent(new KeyEvent(now, now + 30, KeyEvent.ACTION_UP, keyCode, 0));
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
            "(function(t){var e=document.activeElement;while(e&&e.shadowRoot&&e.shadowRoot.activeElement)e=e.shadowRoot.activeElement;" +
            "if(e&&e.tagName&&e.tagName.toLowerCase()=='iframe'){try{e=e.contentDocument.activeElement;}catch(x){}}if(!e)return;" +
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
