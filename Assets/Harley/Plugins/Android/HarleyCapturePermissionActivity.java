package com.harley.capture;

import android.app.Activity;
import android.content.Intent;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;

/** Invisible activity that shows the system "allow screen capture" prompt and hands the result to the service. */
public class HarleyCapturePermissionActivity extends Activity {
    private static final int REQUEST = 4317;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        MediaProjectionManager mpm = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        if (mpm == null) {
            HarleyCapture.lastError = "screen capture not available";
            finish();
            return;
        }
        startActivityForResult(mpm.createScreenCaptureIntent(), REQUEST);
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST) {
            if (resultCode == RESULT_OK && data != null) {
                Intent service = new Intent(this, HarleyCaptureService.class);
                service.putExtra(HarleyCaptureService.EXTRA_RESULT_CODE, resultCode);
                service.putExtra(HarleyCaptureService.EXTRA_RESULT_DATA, data);
                if (Build.VERSION.SDK_INT >= 26) startForegroundService(service); else startService(service);
            } else {
                HarleyCapture.lastError = "declined";
            }
        }
        finish();
    }
}
